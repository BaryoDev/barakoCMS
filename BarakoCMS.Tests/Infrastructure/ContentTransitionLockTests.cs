using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Core.Interfaces;
using barakoCMS.Events;
using barakoCMS.Infrastructure.Jobs;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Infrastructure;

/// <summary>
/// A write that puts an entry in a lifecycle state takes the type lock shared before it reads the
/// lifecycle, so it never lands in a state the type no longer declares.
/// </summary>
/// <remarks>
/// A race test holds the exclusive lock from a second session, starts the write, waits until
/// pg_locks shows the write queued on that key, then changes the stored lifecycle and releases the
/// lock. The write cannot have read the lifecycle before the change, so what it does proves it read
/// it afterwards. Nothing here sleeps: the one wait is a bounded poll of pg_locks for the queued
/// write.
/// </remarks>
[Collection("Integration1")]
public class ContentTransitionLockTests(IntegrationTestFixture factory)
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ContentTransitionOptions Trusted = new() { SkipPermissionChecks = true };

    private IDocumentStore Store => factory.Services.GetRequiredService<IDocumentStore>();

    private static LifecycleDefinition Review() => new()
    {
        States = ["Draft", "Submitted"],
        InitialState = "Draft",
        Transitions = [new StateTransition { Name = "Submit", From = "Draft", To = "Submitted" }],
    };

    /// <summary>The lifecycle with Submitted and the move to it removed.</summary>
    private static LifecycleDefinition WithoutSubmitted() => new()
    {
        States = ["Draft"],
        InitialState = "Draft",
        Transitions = [],
    };

    /// <summary>The lifecycle after an edit that moved the initial state.</summary>
    private static LifecycleDefinition StartsAtIntake() => new()
    {
        States = ["Intake", "Draft", "Submitted"],
        InitialState = "Intake",
        Transitions = [new StateTransition { Name = "Submit", From = "Draft", To = "Submitted" }],
    };

    private async Task<string> TypeAsync(LifecycleDefinition lifecycle, bool withSlug = false, string prefix = "ctl")
    {
        var name = prefix + Guid.NewGuid().ToString("n")[..10];
        var fields = new List<FieldDefinition> { new() { Name = "Title", Type = "string" } };
        if (withSlug)
        {
            fields.Add(new FieldDefinition { Name = "slug", Type = "slug", IsRequired = true });
        }

        using var scope = factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = name,
            DisplayName = "Claim",
            Fields = fields,
            Lifecycle = lifecycle,
        });
        await session.SaveChangesAsync(Ct);
        return name;
    }

    private async Task ReplaceLifecycleAsync(string type, LifecycleDefinition lifecycle)
    {
        using var scope = factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var definition = await session.Query<ContentTypeDefinition>().SingleAsync(d => d.Name == type, Ct);
        definition.Lifecycle = lifecycle;
        session.Store(definition);
        await session.SaveChangesAsync(Ct);
    }

    /// <summary>An entry in the type's initial state, created by a user who is not the one that moves it.</summary>
    private async Task<Guid> EntryAsync(string type)
    {
        using var scope = factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var created = await scope.ServiceProvider.GetRequiredService<IContentCreator>().StageAsync(
            new ContentCreateRequest
            {
                ContentType = type,
                Data = new Dictionary<string, object> { ["Title"] = "a claim" },
            },
            Guid.NewGuid(), batch: null, Ct);
        await session.SaveChangesAsync(Ct);
        return created.Id;
    }

    private async Task<ContentTransitionResult> MoveAsync(Guid id)
    {
        using var scope = factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var content = await session.LoadAsync<Content>(id, Ct);
        content.Should().NotBeNull();
        return await scope.ServiceProvider.GetRequiredService<IContentTransitioner>().TransitionAsync(
            content!, "Submit", ContentTransitionActor.ForSystem("lock-test"), Trusted, Ct);
    }

    private async Task<Content> LoadAsync(Guid id)
    {
        using var scope = factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<Content>(id, Ct))!;
    }

    private async Task<int> TransitionEventsAsync(Guid id)
    {
        using var scope = factory.Services.CreateScope();
        var stream = await scope.ServiceProvider.GetRequiredService<IQuerySession>().Events.FetchStreamAsync(id, token: Ct);
        return stream.Count(e => e.Data is ContentTransitioned);
    }

    private async Task<HttpClient> AdminAsync() => await factory.CreateClientAsync("SuperAdmin", "Admin");

    /// <summary>Holds the exclusive lock, as a change to the type does, until disposed.</summary>
    private async Task<IDisposable> HoldExclusiveAsync(string type)
    {
        var scope = factory.Services.CreateScope();
        await FieldChanges.LockTypeAsync(scope.ServiceProvider.GetRequiredService<IDocumentSession>(), type, Ct);
        return scope;
    }

    private string KeyFor(string type)
    {
        using var scope = factory.Services.CreateScope();
        return $"barakocms:field-change:{scope.ServiceProvider.GetRequiredService<IDocumentSession>().TenantId}:{type.ToLower()}";
    }

    /// <summary>How many sessions hold, or are queued for, the type's lock in a mode that matches.</summary>
    private async Task<long> LocksAsync(string type, bool granted, string mode = "%")
    {
        await using var connection = Store.Storage.Database.CreateConnection();
        await connection.OpenAsync(Ct);
        await using var count = connection.CreateCommand();

        // A bigint advisory key is stored as its high half in classid and its low half in objid.
        count.CommandText = """
            select count(*) from pg_locks
            where locktype = 'advisory' and objsubid = 1 and granted = @granted and mode like @mode
              and classid = ((hashtextextended(@name, 0) >> 32) & 4294967295)::oid
              and objid = (hashtextextended(@name, 0) & 4294967295)::oid
            """;
        count.Parameters.AddWithValue("granted", granted);
        count.Parameters.AddWithValue("mode", mode);
        count.Parameters.AddWithValue("name", KeyFor(type));
        return (long)(await count.ExecuteScalarAsync(Ct))!;
    }

    /// <summary>Returns once <paramref name="writers"/> sessions are queued for the type's lock.</summary>
    private async Task WaitForQueuedAsync(string type, int writers = 1)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        limit.CancelAfter(Limit);
        while (await LocksAsync(type, granted: false) < writers)
        {
            limit.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }

    /// <summary>Whether a second session could take the exclusive lock now.</summary>
    private async Task<bool> CanTakeExclusiveAsync(string type)
    {
        await using var connection = Store.Storage.Database.CreateConnection();
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using var take = connection.CreateCommand();
        take.Transaction = transaction;
        take.CommandText = "select pg_try_advisory_xact_lock(hashtextextended(@name, 0))";
        take.Parameters.AddWithValue("name", KeyFor(type));
        return (bool)(await take.ExecuteScalarAsync(Ct))!;
    }

    [Fact]
    public async Task Transition_reads_the_lifecycle_after_taking_the_type_lock()
    {
        var type = await TypeAsync(Review());
        var id = await EntryAsync(type);

        Task<ContentTransitionResult> move;
        using (await HoldExclusiveAsync(type))
        {
            move = Task.Run(() => MoveAsync(id), Ct);
            await WaitForQueuedAsync(type);
            move.IsCompleted.Should().BeFalse("the move is queued behind the exclusive lock");
            await ReplaceLifecycleAsync(type, WithoutSubmitted());
        }

        var result = await move.WaitAsync(Limit, Ct);

        result.Outcome.Should().Be(ContentTransitionOutcome.Invalid);
        result.Errors.Should().ContainSingle().Which.Should().Contain("is not a transition");
        (await LoadAsync(id)).LifecycleState.Should().Be("Draft");
        (await TransitionEventsAsync(id)).Should().Be(0);
    }

    [Fact]
    public async Task Transition_that_holds_the_lock_makes_a_lifecycle_change_wait()
    {
        var type = await TypeAsync(Review());
        var id = await EntryAsync(type);
        (await CanTakeExclusiveAsync(type)).Should().BeTrue("the positive control: nothing holds the type yet");

        var duringTheMove = false;
        await RunBatchAsync(async (scope, token) =>
        {
            var content = await scope.GetRequiredService<IDocumentSession>().LoadAsync<Content>(id, token);
            var moved = await scope.GetRequiredService<IContentTransitioner>().TransitionAsync(
                content!, "Submit", ContentTransitionActor.ForSystem("lock-test"), Trusted, token);
            moved.Succeeded.Should().BeTrue(string.Join("; ", moved.Errors));

            duringTheMove = await CanTakeExclusiveAsync(type);
        });

        duringTheMove.Should().BeFalse("the move holds the lock until it commits");
        (await LoadAsync(id)).LifecycleState.Should().Be("Submitted");
        (await CanTakeExclusiveAsync(type)).Should().BeTrue("the commit released it");
    }

    [Fact]
    public async Task Concurrent_moves_into_the_same_type_both_complete()
    {
        var type = await TypeAsync(Review());
        var first = await EntryAsync(type);
        var second = await EntryAsync(type);

        long sharedWhileHeld = 0;
        ContentTransitionResult? other = null;
        await RunBatchAsync(async (scope, token) =>
        {
            var content = await scope.GetRequiredService<IDocumentSession>().LoadAsync<Content>(first, token);
            var moved = await scope.GetRequiredService<IContentTransitioner>().TransitionAsync(
                content!, "Submit", ContentTransitionActor.ForSystem("lock-test"), Trusted, token);
            moved.Succeeded.Should().BeTrue(string.Join("; ", moved.Errors));

            sharedWhileHeld = await LocksAsync(type, granted: true, "ShareLock");

            // The first move still holds its lock. An exclusive lock in the writer would hold this one forever.
            other = await MoveAsync(second).WaitAsync(Limit, token);
        });

        sharedWhileHeld.Should().Be(1, "the first move holds the lock shared");
        other!.Succeeded.Should().BeTrue(string.Join("; ", other.Errors));
        (await LoadAsync(first)).LifecycleState.Should().Be("Submitted");
        (await LoadAsync(second)).LifecycleState.Should().Be("Submitted");
    }

    [Fact]
    public async Task Batch_transition_step_reads_the_lifecycle_after_taking_the_type_lock()
    {
        var type = await TypeAsync(Review());
        var id = await EntryAsync(type);
        var client = await AdminAsync();

        Task<HttpResponseMessage> batch;
        using (await HoldExclusiveAsync(type))
        {
            batch = client.PostAsJsonAsync("/api/contents/batch",
                new { steps = new object[] { new { op = "transition", id, transition = "Submit" } } }, Ct);
            await WaitForQueuedAsync(type);
            batch.IsCompleted.Should().BeFalse("the batch is queued behind the exclusive lock");
            await ReplaceLifecycleAsync(type, WithoutSubmitted());
        }

        var response = await batch.WaitAsync(Limit, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync(Ct));
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("is not a transition");
        (await LoadAsync(id)).LifecycleState.Should().Be("Draft");
        (await TransitionEventsAsync(id)).Should().Be(0);
    }

    [Fact]
    public async Task Batch_create_step_reads_the_lifecycle_after_taking_the_type_lock()
    {
        var type = await TypeAsync(Review());
        var id = Guid.CreateVersion7();
        var client = await AdminAsync();

        Task<HttpResponseMessage> batch;
        using (await HoldExclusiveAsync(type))
        {
            batch = client.PostAsJsonAsync("/api/contents/batch", new
            {
                steps = new object[]
                {
                    new { op = "create", contentType = type, id, data = new Dictionary<string, object> { ["Title"] = "a claim" } },
                },
            }, Ct);
            await WaitForQueuedAsync(type);
            batch.IsCompleted.Should().BeFalse("the batch is queued behind the exclusive lock");
            await ReplaceLifecycleAsync(type, StartsAtIntake());
        }

        var response = await batch.WaitAsync(Limit, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        (await LoadAsync(id)).LifecycleState.Should().Be("Intake", "the state is the one the type declares after the edit");
    }

    [Fact]
    public async Task Create_reads_the_initial_state_after_taking_the_type_lock()
    {
        var type = await TypeAsync(Review());
        var client = await AdminAsync();

        Task<HttpResponseMessage> create;
        using (await HoldExclusiveAsync(type))
        {
            create = client.PostAsJsonAsync("/api/contents", new
            {
                contentType = type,
                data = new Dictionary<string, object> { ["Title"] = "a claim" },
            }, Ct);
            await WaitForQueuedAsync(type);
            create.IsCompleted.Should().BeFalse("the create is queued behind the exclusive lock");
            await ReplaceLifecycleAsync(type, StartsAtIntake());
        }

        var response = await create.WaitAsync(Limit, Ct);

        response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync(Ct));
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var created = await LoadAsync(body.RootElement.GetProperty("id").GetGuid());
        created.LifecycleState.Should().Be("Intake", "the state is the one the type declares after the edit");
    }

    [Fact]
    public async Task Collections_push_reads_the_initial_state_after_taking_the_type_lock()
    {
        var type = await TypeAsync(Review(), withSlug: true);
        var client = await AdminAsync();
        var slug = "s-" + Guid.NewGuid().ToString("n")[..8];

        Task<HttpResponseMessage> push;
        using (await HoldExclusiveAsync(type))
        {
            push = client.PostAsJsonAsync($"/api/collections/{type}/push", new
            {
                entries = new[] { new Dictionary<string, object> { ["slug"] = slug, ["Title"] = "a claim" } },
            }, Ct);
            await WaitForQueuedAsync(type);
            push.IsCompleted.Should().BeFalse("the push is queued behind the exclusive lock");
            await ReplaceLifecycleAsync(type, StartsAtIntake());
        }

        var response = await push.WaitAsync(Limit, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        using var scope = factory.Services.CreateScope();
        var entries = await scope.ServiceProvider.GetRequiredService<IQuerySession>().Query<Content>()
            .Where(c => c.ContentType == type)
            .ToListAsync(Ct);
        entries.Should().HaveCount(1);
        entries[0].LifecycleState.Should().Be("Intake", "the state is the one the type declares after the edit");
    }

    [Fact]
    public async Task Batches_over_two_types_in_opposite_orders_lock_them_in_name_order_and_both_complete()
    {
        var low = await TypeAsync(Review(), prefix: "ordaa");
        var high = await TypeAsync(Review(), prefix: "ordzz");
        var (lowA, lowB) = (await EntryAsync(low), await EntryAsync(low));
        var (highA, highB) = (await EntryAsync(high), await EntryAsync(high));
        var client = await AdminAsync();

        object Move(Guid id) => new { op = "transition", id, transition = "Submit" };

        Task<HttpResponseMessage> opposite;
        Task<HttpResponseMessage> inOrder;
        long highHeldWhileQueued;
        using (await HoldExclusiveAsync(low))
        {
            // The first names the later type first. Each batch moves entries of its own, so the
            // only thing they can wait on is a type lock. Both have to queue on the earlier type's
            // lock before either takes the later one.
            opposite = client.PostAsJsonAsync("/api/contents/batch",
                new { steps = new[] { Move(highA), Move(lowA) } }, Ct);
            inOrder = client.PostAsJsonAsync("/api/contents/batch",
                new { steps = new[] { Move(lowB), Move(highB) } }, Ct);

            await WaitForQueuedAsync(low, writers: 2);
            highHeldWhileQueued = await LocksAsync(high, granted: true);
        }

        highHeldWhileQueued.Should().Be(0, "no batch holds the later type while it waits for the earlier one");

        var answers = await Task.WhenAll(opposite, inOrder).WaitAsync(Limit, Ct);

        answers.Should().NotBeEmpty().And.OnlyContain(a => a.StatusCode == HttpStatusCode.OK);
        foreach (var id in new[] { lowA, lowB, highA, highB })
        {
            (await LoadAsync(id)).LifecycleState.Should().Be("Submitted");
        }
    }

    /// <summary>How many advisory locks are held by the session that is queued for the type's lock.</summary>
    private async Task<long> AdvisoryLocksHeldByWaiterAsync(string type)
    {
        await using var connection = Store.Storage.Database.CreateConnection();
        await connection.OpenAsync(Ct);
        await using var count = connection.CreateCommand();
        count.CommandText = """
            select count(*) from pg_locks
            where locktype = 'advisory' and granted
              and pid in (
                select pid from pg_locks
                where locktype = 'advisory' and objsubid = 1 and not granted
                  and classid = ((hashtextextended(@name, 0) >> 32) & 4294967295)::oid
                  and objid = (hashtextextended(@name, 0) & 4294967295)::oid)
            """;
        count.Parameters.AddWithValue("name", KeyFor(type));
        return (long)(await count.ExecuteScalarAsync(Ct))!;
    }

    [Fact]
    public async Task Create_takes_the_type_lock_before_a_hook_takes_an_advisory_lock_of_its_own()
    {
        var pages = new BarakoCMS.Tests.PagesModuleTests(factory);
        await pages.DeclareTypeAsync();
        var type = BarakoCMS.Tests.PagesModuleTests.TypeName;
        var client = await AdminAsync();

        var parent = await client.PostAsJsonAsync("/api/contents", new
        {
            contentType = type,
            data = new Dictionary<string, object> { ["Title"] = "root", ["Slug"] = "r-" + Guid.NewGuid().ToString("n")[..8] },
        }, Ct);
        parent.IsSuccessStatusCode.Should().BeTrue(await parent.Content.ReadAsStringAsync(Ct));
        using var parentBody = JsonDocument.Parse(await parent.Content.ReadAsStringAsync(Ct));
        var parentId = parentBody.RootElement.GetProperty("id").GetGuid();

        Task<HttpResponseMessage> child;
        long heldWhileQueued;
        using (await HoldExclusiveAsync(type))
        {
            // The page tree hook walks the parent chain under an advisory lock of its own. Queued on
            // the type's lock, the create has to be holding none yet, or a move that holds the type
            // and then runs the same hook could wait on it while the create waits on the type.
            child = client.PostAsJsonAsync("/api/contents", new
            {
                contentType = type,
                data = new Dictionary<string, object>
                {
                    ["Title"] = "child", ["Slug"] = "c-" + Guid.NewGuid().ToString("n")[..8], ["ParentPage"] = parentId.ToString(),
                },
            }, Ct);
            await WaitForQueuedAsync(type);
            heldWhileQueued = await AdvisoryLocksHeldByWaiterAsync(type);
        }

        heldWhileQueued.Should().Be(0, "the create asks for the type before any hook runs");
        var response = await child.WaitAsync(Limit, Ct);
        response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Staging_a_create_directly_waits_for_the_type_lock()
    {
        var type = await TypeAsync(Review());

        Task<Guid> stage;
        using (await HoldExclusiveAsync(type))
        {
            // No CheckAsync first, as a caller that stages on its own does it.
            stage = Task.Run(() => EntryAsync(type), Ct);
            await WaitForQueuedAsync(type);
            stage.IsCompleted.Should().BeFalse("the stage is queued behind the exclusive lock");
        }

        (await stage.WaitAsync(Limit, Ct)).Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_plain_request_session_holds_the_shared_type_lock_until_it_is_disposed()
    {
        var type = await TypeAsync(Review());

        var scope = factory.Services.CreateScope();
        try
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            await FieldChanges.LockTypeSharedAsync(session, type, Ct);

            (await CanTakeExclusiveAsync(type)).Should().BeFalse("the session holds the lock after the call returns");
            (await LocksAsync(type, granted: true, "ShareLock")).Should().Be(1);
        }
        finally
        {
            scope.Dispose();
        }

        (await CanTakeExclusiveAsync(type)).Should().BeTrue("ending the session released it");
    }

    [Fact]
    public async Task Batches_of_create_steps_over_two_types_in_opposite_orders_lock_them_in_name_order()
    {
        var low = await TypeAsync(Review(), prefix: "crtaa");
        var high = await TypeAsync(Review(), prefix: "crtzz");
        var client = await AdminAsync();

        object Create(string type) => new
        {
            op = "create", contentType = type, id = Guid.NewGuid(),
            data = new Dictionary<string, object> { ["Title"] = "a claim" },
        };

        Task<HttpResponseMessage> batch;
        long highHeldWhileQueued;
        using (await HoldExclusiveAsync(low))
        {
            batch = client.PostAsJsonAsync("/api/contents/batch", new { steps = new[] { Create(high), Create(low) } }, Ct);
            await WaitForQueuedAsync(low);
            highHeldWhileQueued = await LocksAsync(high, granted: true);
        }

        highHeldWhileQueued.Should().Be(0, "the batch holds no later type while it waits for the earlier one");
        var response = await batch.WaitAsync(Limit, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>Stores a header type that declares lines of a second type, and that line type.</summary>
    private async Task StoreLineTypesAsync(string header, string line)
    {
        using var scope = factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(), Name = header, DisplayName = header,
            Fields = [new FieldDefinition { Name = "Title", Type = "string" }],
            Lines = new LinesDefinition { ContentType = line, ParentField = "Order" },
        });
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(), Name = line, DisplayName = line,
            Fields =
            [
                new FieldDefinition { Name = "Title", Type = "string" },
                new FieldDefinition { Name = "Order", Type = "reference", ReferenceType = header },
            ],
        });
        await session.SaveChangesAsync(Ct);
    }

    /// <summary>Creates a header with one line while the exclusive lock on <paramref name="held"/> is held.</summary>
    private async Task<(long OtherHeld, HttpResponseMessage Response)> CreateWithLineWhileHoldingAsync(
        string header, string held, string other)
    {
        var client = await AdminAsync();
        Task<HttpResponseMessage> create;
        long otherHeldWhileQueued;
        using (await HoldExclusiveAsync(held))
        {
            create = client.PostAsJsonAsync("/api/contents", new
            {
                contentType = header,
                data = new Dictionary<string, object> { ["Title"] = "an order" },
                lines = new object[] { new Dictionary<string, object> { ["Title"] = "a line" } },
            }, Ct);
            await WaitForQueuedAsync(held);
            otherHeldWhileQueued = await LocksAsync(other, granted: true);
        }

        return (otherHeldWhileQueued, await create.WaitAsync(Limit, Ct));
    }

    [Fact]
    public async Task A_header_with_lines_locks_its_types_in_name_order_whatever_order_it_writes_them()
    {
        // LineWriter hands the types over line type first, then the header, so the later-named type
        // is the line's and the order it passes them in is the reverse of name order.
        var header = "lwaa" + Guid.NewGuid().ToString("n")[..10];
        var line = "lwzz" + Guid.NewGuid().ToString("n")[..10];
        await StoreLineTypesAsync(header, line);

        var (lineHeld, response) = await CreateWithLineWhileHoldingAsync(header, held: header, other: line);

        lineHeld.Should().Be(0, "the write holds no later type while it waits for the earlier one");
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_header_named_after_its_lines_still_locks_the_lines_type_first()
    {
        // The header is written first, so without a pre-lock it would take the later-named type first.
        var line = "lwaa" + Guid.NewGuid().ToString("n")[..10];
        var header = "lwzz" + Guid.NewGuid().ToString("n")[..10];
        await StoreLineTypesAsync(header, line);

        var (headerHeld, response) = await CreateWithLineWhileHoldingAsync(header, held: line, other: header);

        headerHeld.Should().Be(0, "the write holds no later type while it waits for the earlier one");
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task An_import_over_two_types_locks_them_in_name_order_whatever_order_it_writes_them()
    {
        var low = await TypeAsync(Review(), prefix: "impaa");
        var high = await TypeAsync(Review(), prefix: "impzz");
        var client = await AdminAsync();

        object Record(string type) => new
        {
            contentType = type, status = "Published",
            data = new Dictionary<string, object> { ["Title"] = "a claim" },
        };

        Task<HttpResponseMessage> import;
        long highHeldWhileQueued;
        using (await HoldExclusiveAsync(low))
        {
            import = client.PostAsJsonAsync("/api/portability/import", new
            {
                dryRun = false,
                contentTypes = Array.Empty<object>(),
                contents = new[] { Record(high), Record(low) },
            }, Ct);
            await WaitForQueuedAsync(low);
            highHeldWhileQueued = await LocksAsync(high, granted: true);
        }

        highHeldWhileQueued.Should().Be(0, "the import holds no later type while it waits for the earlier one");
        var response = await import.WaitAsync(Limit, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
    }

    private async Task RunBatchAsync(Func<IServiceProvider, CancellationToken, Task> work)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IContentBatchRunner>().RunAsync(async (provider, token) =>
        {
            await work(provider, token);
            return new ContentBatchOutcome<bool>(true, Commit: true);
        }, Ct);
    }
}
