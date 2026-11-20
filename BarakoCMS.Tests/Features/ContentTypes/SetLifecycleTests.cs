using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using BarakoCMS.Portability;
using barakoCMS.Core.Interfaces;
using barakoCMS.Events;
using barakoCMS.Features.Monitoring.Meta;
using barakoCMS.Infrastructure.Jobs;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using BarakoCMS.Tests.Features.Tenants;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.ContentTypes;

/// <summary>
/// PUT /api/content-types/{name}/lifecycle, which replaces the lifecycle of a type that exists.
/// </summary>
/// <remarks>
/// The edit rewrites no entry, so what it has to prove is what it refuses: an entry left in a state
/// the type no longer declares, and an edit that races a move or another edit. Entries are stored
/// straight into Marten with the state the test wants, because the route under test is the only
/// thing that changes a lifecycle and the creator would put every one in the initial state. The lock
/// test holds the type's shared lock from a move in flight and waits on pg_locks for the edit to
/// queue behind it, so nothing here sleeps.
/// </remarks>
[Collection("Integration1")]
public class SetLifecycleTests(IntegrationTestFixture factory)
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ContentTransitionOptions Trusted = new() { SkipPermissionChecks = true };

    private IDocumentStore Store => factory.Services.GetRequiredService<IDocumentStore>();

    private static string NewName(string prefix = "lc") => prefix + Guid.NewGuid().ToString("n")[..10];

    private static LifecycleDefinition Small() => new()
    {
        States = ["Draft", "Submitted"],
        InitialState = "Draft",
        Transitions = [new StateTransition { Name = "Submit", From = "Draft", To = "Submitted" }],
    };

    private static LifecycleDefinition Claim() => new()
    {
        States = ["Draft", "Review", "Approved"],
        InitialState = "Draft",
        Transitions =
        [
            new StateTransition { Name = "Submit", From = "Draft", To = "Review" },
            new StateTransition { Name = "Approve", From = "Review", To = "Approved" },
        ],
    };

    /// <summary>The claim lifecycle without Review and Approved and the moves to them.</summary>
    private static LifecycleDefinition DraftOnly() => new() { States = ["Draft"], InitialState = "Draft", Transitions = [] };

    // ---- setup ------------------------------------------------------------------------------

    private async Task<string> TypeAsync(
        LifecycleDefinition? lifecycle, string? tenant = null, bool deliverable = false, bool eventSourced = false)
    {
        var name = NewName();
        await using var session = SessionIn(tenant);
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = name,
            DisplayName = "Claim",
            IsPubliclyDeliverable = deliverable,
            Fields =
            [
                new FieldDefinition { Name = "Title", Type = "string" },
                new FieldDefinition { Name = "Reason", Type = "string" },
            ],
            Lifecycle = lifecycle,
        });
        if (eventSourced)
        {
            session.Store(new ContentTypeSourcingPolicy { Name = name, EventSourced = true });
        }

        await session.SaveChangesAsync(Ct);
        return name;
    }

    private IDocumentSession SessionIn(string? tenant) =>
        tenant is null ? Store.LightweightSession() : Store.LightweightSession(tenant);

    /// <summary>Stores an entry holding exactly this state, or none when it is null.</summary>
    private async Task<Guid> StoredEntryAsync(
        string type, string? state, string? tenant = null, Guid? branch = null, ContentStatus status = ContentStatus.Draft)
    {
        var id = Guid.NewGuid();
        await using var session = SessionIn(tenant);
        session.Store(new Content
        {
            Id = id,
            ContentType = type,
            Data = new Dictionary<string, object> { ["Title"] = "an entry" },
            LifecycleState = state,
            BranchId = branch,
            Status = status,
        });
        await session.SaveChangesAsync(Ct);
        return id;
    }

    /// <summary>An entry made by the creator, so it has an event stream and the type's initial state.</summary>
    private async Task<Guid> CreatedEntryAsync(string type)
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

    private async Task<ContentTransitionResult> MoveAsync(Guid id, string transition)
    {
        using var scope = factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var content = await session.LoadAsync<Content>(id, Ct);
        content.Should().NotBeNull();
        return await scope.ServiceProvider.GetRequiredService<IContentTransitioner>().TransitionAsync(
            content!, transition, ContentTransitionActor.ForSystem("lifecycle-test"), Trusted, Ct);
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

    private async Task<ContentTypeDefinition> StoredAsync(string type, string? tenant = null)
    {
        await using var session = tenant is null ? Store.QuerySession() : Store.QuerySession(tenant);
        return await session.Query<ContentTypeDefinition>().SingleAsync(d => d.Name == type, Ct);
    }

    private async Task<Guid> VersionOfAsync(string type, string? tenant = null)
    {
        await using var session = tenant is null ? Store.QuerySession() : Store.QuerySession(tenant);
        var stored = await session.Query<ContentTypeDefinition>().SingleAsync(d => d.Name == type, Ct);
        return (await session.MetadataForAsync(stored, Ct)).CurrentVersion;
    }

    private async Task<List<AuditEvent>> AuditAsync(string type)
    {
        var id = (await StoredAsync(type)).Id.ToString();
        await using var session = Store.QuerySession();
        var rows = await session.Query<AuditEvent>()
            .Where(e => e.Action == "contenttype.lifecycle.changed" && e.TargetId == id)
            .ToListAsync(Ct);
        return rows.OrderBy(e => e.CreatedAt).ToList();
    }

    private async Task<(HttpClient Client, User User)> AdminWithUserAsync()
    {
        using var scope = factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var roleIds = new List<Guid>();
        foreach (var name in new[] { "SuperAdmin", "Admin" })
        {
            var role = await session.Query<Role>().FirstOrDefaultAsync(r => r.Name == name, Ct);
            if (role is null)
            {
                role = new Role { Id = Guid.NewGuid(), Name = name };
                session.Store(role);
            }

            roleIds.Add(role.Id);
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = $"lc_{Guid.NewGuid():n}",
            Email = $"lc_{Guid.NewGuid():n}@example.com",
            RoleIds = roleIds,
        };
        session.Store(user);
        await session.SaveChangesAsync(Ct);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", factory.CreateToken(
                roles: ["SuperAdmin", "Admin"], userId: user.Id.ToString(),
                additionalClaims: new Dictionary<string, string> { ["Username"] = user.Username }));
        return (client, user);
    }

    private async Task<HttpClient> AdminAsync() => await factory.CreateClientAsync("SuperAdmin", "Admin");

    // ---- requests and reading answers --------------------------------------------------------

    /// <summary>
    /// Sends the edit with the type's current version as If-Match, as a client that just loaded it
    /// would. <paramref name="version"/> sends another, and <paramref name="noVersion"/> sends none.
    /// </summary>
    private async Task<HttpResponseMessage> PutAsync(
        HttpClient client, string type, LifecycleDefinition? lifecycle, bool dryRun = false, string? key = null,
        Guid? version = null, bool noVersion = false, string? tenant = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/content-types/{type}/lifecycle")
        {
            Content = JsonContent.Create(new { lifecycle, dryRun }),
        };
        if (!noVersion)
        {
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{version ?? await VersionOfAsync(type, tenant):D}\"");
        }

        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        return await client.SendAsync(request, Ct);
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int CountOf(JsonElement blocker) =>
        blocker.TryGetProperty("count", out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;

    private static List<(string Kind, string? State, int Count)> Blockers(JsonElement body) =>
        body.GetProperty("blockers").EnumerateArray()
            .Select(b => (Str(b, "kind")!, Str(b, "state"), CountOf(b)))
            .ToList();

    private static async Task<List<(string Kind, string? State, int Count)>> RefusedWithAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(HttpStatusCode.Conflict, text);
        return Blockers(JsonDocument.Parse(text).RootElement);
    }

    // ---- edits that work ---------------------------------------------------------------------

    [Fact]
    public async Task Edit_adds_a_state_and_a_transition_and_a_move_through_it_works()
    {
        var admin = await AdminAsync();
        var type = await TypeAsync(Small());
        var id = await CreatedEntryAsync(type);
        (await MoveAsync(id, "Submit")).Succeeded.Should().BeTrue();
        (await MoveAsync(id, "Approve")).Succeeded.Should().BeFalse("the type has no Approve yet, the control for the move below");

        var edited = new LifecycleDefinition
        {
            States = ["Draft", "Submitted", "Approved"],
            InitialState = "Draft",
            Transitions =
            [
                new StateTransition { Name = "Submit", From = "Draft", To = "Submitted" },
                new StateTransition { Name = "Approve", From = "Submitted", To = "Approved" },
            ],
        };
        var response = await PutAsync(admin, type, edited);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var body = await BodyAsync(response);
        body.GetProperty("changed").GetBoolean().Should().BeTrue();
        body.GetProperty("blockers").GetArrayLength().Should().Be(0);
        var moved = await MoveAsync(id, "Approve");
        moved.Succeeded.Should().BeTrue(string.Join("; ", moved.Errors));

        await AssertReadsApprovedAsync(admin, type, id);
    }

    private async Task AssertReadsApprovedAsync(HttpClient admin, string type, Guid id)
    {
        var get = await BodyAsync(await admin.GetAsync($"/api/contents/{id}", Ct));
        Str(get, "lifecycleState").Should().Be("Approved");

        var list = await (await admin.GetAsync($"/api/contents?contentType={type}", Ct)).Content.ReadAsStringAsync(Ct);
        list.Should().Contain(id.ToString()).And.Contain("Approved");

        // The change feed reads these events, so the stream is what shows the move there.
        using var scope = factory.Services.CreateScope();
        var stream = await scope.ServiceProvider.GetRequiredService<IQuerySession>().Events.FetchStreamAsync(id, token: Ct);
        stream.Select(e => e.Data).OfType<ContentTransitioned>().Select(e => e.ToState)
            .Should().Equal("Submitted", "Approved");
    }

    [Fact]
    public async Task Edit_without_a_lifecycle_on_the_type_creates_one_only_when_no_entries_exist()
    {
        var admin = await AdminAsync();
        var empty = await TypeAsync(null);
        var held = await TypeAsync(null);
        await StoredEntryAsync(held, null);

        var created = await PutAsync(admin, empty, Small());
        var refused = await PutAsync(admin, held, Small());

        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync(Ct));
        (await StoredAsync(empty)).Lifecycle!.States.Should().Equal("Draft", "Submitted");
        (await RefusedWithAsync(refused)).Should().ContainSingle().Which.Kind.Should().Be("initialState");
        (await StoredAsync(held)).Lifecycle.Should().BeNull();
    }

    [Fact]
    public async Task Edit_allows_removing_and_renaming_a_state_no_entry_holds()
    {
        var admin = await AdminAsync();
        var type = await TypeAsync(Claim());
        await StoredEntryAsync(type, "Draft");

        var renamed = new LifecycleDefinition
        {
            States = ["Draft", "Triage", "Approved"],
            InitialState = "Draft",
            Transitions =
            [
                new StateTransition { Name = "Submit", From = "Draft", To = "Triage" },
                new StateTransition { Name = "Approve", From = "Triage", To = "Approved" },
            ],
        };
        (await PutAsync(admin, type, renamed)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await StoredAsync(type)).Lifecycle!.States.Should().Equal("Draft", "Triage", "Approved");

        (await PutAsync(admin, type, DraftOnly())).StatusCode.Should().Be(HttpStatusCode.OK);
        (await StoredAsync(type)).Lifecycle!.States.Should().Equal("Draft");
    }

    [Fact]
    public async Task Edit_allows_reordering_states_and_transitions()
    {
        var admin = await AdminAsync();
        var type = await TypeAsync(Claim());
        await StoredEntryAsync(type, "Review");
        var reordered = Claim();
        reordered.States.Reverse();
        reordered.Transitions.Reverse();

        var response = await PutAsync(admin, type, reordered);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        (await BodyAsync(response)).GetProperty("changed").GetBoolean().Should().BeTrue("an order change is a change");
        var stored = (await StoredAsync(type)).Lifecycle!;
        stored.States.Should().Equal("Approved", "Review", "Draft");
        stored.Transitions.Select(t => t.Name).Should().Equal("Approve", "Submit");
    }

    [Fact]
    public async Task Edit_allows_changing_the_spelling_case_of_a_state_no_entry_holds()
    {
        var admin = await AdminAsync();
        var type = await TypeAsync(Claim());
        await StoredEntryAsync(type, "Draft");
        var recased = new LifecycleDefinition
        {
            States = ["Draft", "Review", "APPROVED"],
            InitialState = "Draft",
            Transitions =
            [
                new StateTransition { Name = "Submit", From = "Draft", To = "Review" },
                new StateTransition { Name = "Approve", From = "Review", To = "APPROVED" },
            ],
        };

        var response = await PutAsync(admin, type, recased);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        (await StoredAsync(type)).Lifecycle!.States.Should().Equal("Draft", "Review", "APPROVED");
    }

    // ---- state refusals ----------------------------------------------------------------------

    [Fact]
    public async Task Edit_refuses_removing_a_state_an_entry_holds_and_counts_entries_per_state()
    {
        var admin = await AdminAsync();
        var type = await TypeAsync(Claim());
        await StoredEntryAsync(type, "Review");
        await StoredEntryAsync(type, "Review");
        await StoredEntryAsync(type, "Approved");
        await StoredEntryAsync(type, "Draft");

        var response = await PutAsync(admin, type, DraftOnly());

        var blockers = await RefusedWithAsync(response);
        blockers.Should().BeEquivalentTo(new[]
        {
            ("entryState", (string?)"Review", 2),
            ("entryState", (string?)"Approved", 1),
        });
        var body = await BodyAsync(await PutAsync(admin, type, DraftOnly()));
        body.GetProperty("errors").GetArrayLength().Should().Be(2, "each blocker is also an error, as every other 409 lists them");
        (await StoredAsync(type)).Lifecycle!.States.Should().Equal("Draft", "Review", "Approved");
    }

    [Fact]
    public async Task Edit_counts_state_case_insensitively()
    {
        var admin = await AdminAsync();
        var type = await TypeAsync(Claim());
        await StoredEntryAsync(type, "approved");

        var blockers = await RefusedWithAsync(await PutAsync(admin, type, WithoutApproved()));

        blockers.Should().ContainSingle().Which.Should().Be(("entryState", "Approved", 1));
    }

    private static LifecycleDefinition WithoutApproved() => new()
    {
        States = ["Draft", "Review"],
        InitialState = "Draft",
        Transitions = [new StateTransition { Name = "Submit", From = "Draft", To = "Review" }],
    };

    [Fact]
    public async Task Edit_counts_entries_with_no_stored_state_as_the_old_initial_state()
    {
        var admin = await AdminAsync();
        var type = await TypeAsync(Claim());
        await StoredEntryAsync(type, null);
        await StoredEntryAsync(type, "");
        var withoutDraft = new LifecycleDefinition
        {
            States = ["Review", "Approved"],
            InitialState = "Review",
            Transitions = [new StateTransition { Name = "Approve", From = "Review", To = "Approved" }],
        };

        var blockers = await RefusedWithAsync(await PutAsync(admin, type, withoutDraft));

        blockers.Should().Contain(("entryState", "Draft", 2), "an entry with no state, or an empty one, is in the initial state");
        (await StoredAsync(type)).Lifecycle!.InitialState.Should().Be("Draft");
    }

    [Fact]
    public async Task Edit_counts_only_this_tenant_and_every_branch()
    {
        var admin = await AdminAsync();
        var elsewhere = await TypeAsync(Claim());
        var other = "other-" + Guid.NewGuid().ToString("n")[..8];
        await StoredEntryAsync(elsewhere, "Approved", tenant: other);

        var allowed = await PutAsync(admin, elsewhere, WithoutApproved());
        allowed.StatusCode.Should().Be(HttpStatusCode.OK, "an entry of another tenant with the same state name is not this type's");

        var branched = await TypeAsync(Claim());
        await StoredEntryAsync(branched, "Approved", branch: Guid.NewGuid());

        var blockers = await RefusedWithAsync(await PutAsync(admin, branched, WithoutApproved()));
        blockers.Should().ContainSingle().Which.Should().Be(("entryState", "Approved", 1));
    }

    [Fact]
    public async Task Edit_refuses_renaming_a_state_an_entry_holds_including_a_case_only_rename()
    {
        var admin = await AdminAsync();
        var type = await TypeAsync(Claim());
        await StoredEntryAsync(type, "Review");
        var renamed = Claim();
        renamed.States[1] = "Triage";
        renamed.Transitions[0].To = "Triage";
        renamed.Transitions[1].From = "Triage";
        var recased = Claim();
        recased.States[1] = "REVIEW";
        recased.Transitions[0].To = "REVIEW";
        recased.Transitions[1].From = "REVIEW";

        var rename = await RefusedWithAsync(await PutAsync(admin, type, renamed));
        var caseOnly = await RefusedWithAsync(await PutAsync(admin, type, recased));

        rename.Should().ContainSingle().Which.Should().Be(("entryState", "Review", 1));
        caseOnly.Should().ContainSingle().Which.Should().Be(("entryState", "Review", 1), "a case-only rename is a rename");
        (await StoredAsync(type)).Lifecycle!.States[1].Should().Be("Review");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Edit_refuses_changing_the_initial_state_when_the_type_has_entries(bool eventSourced)
    {
        var admin = await AdminAsync();
        var type = await TypeAsync(Claim(), eventSourced: eventSourced);
        var free = await TypeAsync(Claim());
        await CreatedEntryAsync(type);
        var intake = Claim();
        intake.InitialState = "Review";

        var blockers = await RefusedWithAsync(await PutAsync(admin, type, intake));
        var allowed = await PutAsync(admin, free, intake);

        blockers.Should().ContainSingle().Which.Should().Be(("initialState", "Draft", 1));
        (await StoredAsync(type)).Lifecycle!.InitialState.Should().Be("Draft");
        allowed.StatusCode.Should().Be(HttpStatusCode.OK, "the positive control: a type with no entries may move its initial state");
    }

    // ---- transition fields -------------------------------------------------------------------

    [Fact]
    public async Task Edit_refuses_a_transition_field_the_type_does_not_have()
    {
        var admin = await AdminAsync();
        var type = await TypeAsync(Claim());
        var edited = Claim();
        edited.Transitions[0].RequiredFields = ["NoSuchField"];

        var response = await PutAsync(admin, type, edited);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("NoSuchField");
        (await StoredAsync(type)).Lifecycle!.Transitions[0].RequiredFields.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task Edit_allows_changing_required_and_optional_fields_of_a_transition_nothing_else_names()
    {
        var admin = await AdminAsync();
        var type = await TypeAsync(Claim());
        await StoredEntryAsync(type, "Draft");
        var edited = Claim();
        edited.Transitions[0].RequiredFields = ["Title"];
        edited.Transitions[0].OptionalFields = ["Reason"];

        var response = await PutAsync(admin, type, edited);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var stored = (await StoredAsync(type)).Lifecycle!.Transitions[0];
        stored.RequiredFields.Should().Equal("Title");
        stored.OptionalFields.Should().Equal("Reason");
    }

    // ---- type ownership and field changes ----------------------------------------------------

    [Fact]
    public async Task Edit_refuses_a_module_owned_type_like_any_module_type()
    {
        var admin = await AdminAsync();
        var name = NewName("owned");
        await using (var session = Store.LightweightSession())
        {
            var accountId = await AccountContentTypes.AccountOfAsync(session, AccountContentTypes.TenantSlugOf(session), Ct);
            session.Store(new AccountContentType
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                Name = name,
                Definition = new ContentTypeDefinition { Id = Guid.NewGuid(), Name = name, DisplayName = name, Lifecycle = Small() },
            });
            await session.SaveChangesAsync(Ct);
        }

        // Neither type has a stored definition to read a version from, so any version is sent.
        var refused = await PutAsync(admin, name, Claim(), version: Guid.NewGuid());
        var unknown = await PutAsync(admin, NewName("none"), Claim(), version: Guid.NewGuid());

        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden, await refused.Content.ReadAsStringAsync(Ct));
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound, "the control: a name that is nobody's is a 404, not a 403");
    }

    [Fact]
    public async Task Edit_is_refused_while_a_field_change_runs()
    {
        var admin = await AdminAsync();
        var type = await TypeAsync(Small());

        // Already running when the edit arrives: the gate refuses it before the endpoint runs.
        var first = await StoreRunningChangeAsync(type);
        var gated = await PutAsync(admin, type, Claim());
        gated.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await gated.Content.ReadAsStringAsync(Ct)).Should().Contain("field change running");
        await CompleteChangeAsync(first);

        // Starting while the edit waits for the lock: the gate has passed, so the endpoint's own check refuses it.
        Task<HttpResponseMessage> raced;
        Guid second;
        using (await HoldExclusiveAsync(type))
        {
            raced = Task.Run(() => PutAsync(admin, type, Claim()), Ct);
            await WaitForQueuedAsync(type);
            raced.IsCompleted.Should().BeFalse("the edit is queued behind the lock");
            second = await StoreRunningChangeAsync(type);
        }

        var refused = await raced.WaitAsync(Limit, Ct);
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await refused.Content.ReadAsStringAsync(Ct)).Should().Contain("field change running");
        (await StoredAsync(type)).Lifecycle!.States.Should().Equal("Draft", "Submitted");

        await CompleteChangeAsync(second);
        (await PutAsync(admin, type, Claim())).StatusCode.Should().Be(HttpStatusCode.OK, "the positive control");
    }

    private async Task<Guid> StoreRunningChangeAsync(string type)
    {
        var change = new FieldChange
        {
            Id = Guid.NewGuid(), ContentType = type, Field = "Reason", Kind = FieldChangeKind.Rename, NewName = "Cause",
            State = FieldChangeState.Running, Total = 1, StartedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        await using var session = Store.LightweightSession();
        session.Store(change);
        await session.SaveChangesAsync(Ct);
        return change.Id;
    }

    private async Task CompleteChangeAsync(Guid id)
    {
        await using var session = Store.LightweightSession();
        var change = (await session.LoadAsync<FieldChange>(id, Ct))!;
        change.State = FieldChangeState.Completed;
        session.Store(change);
        await session.SaveChangesAsync(Ct);
    }

    /// <summary>Holds the exclusive lock, as a change to the type does, until disposed.</summary>
    private async Task<IDisposable> HoldExclusiveAsync(string type)
    {
        var scope = factory.Services.CreateScope();
        await FieldChanges.LockTypeAsync(scope.ServiceProvider.GetRequiredService<IDocumentSession>(), type, Ct);
        return scope;
    }

    // ---- concurrency and dry run -------------------------------------------------------------

    [Fact]
    public async Task Edit_takes_the_exclusive_type_lock_and_waits_for_a_move_in_flight()
    {
        var admin = await AdminAsync();
        var type = await TypeAsync(Small());
        var id = await CreatedEntryAsync(type);

        Task<HttpResponseMessage>? edit = null;
        await RunBatchAsync(async (scope, token) =>
        {
            var content = await scope.GetRequiredService<IDocumentSession>().LoadAsync<Content>(id, token);
            var moved = await scope.GetRequiredService<IContentTransitioner>().TransitionAsync(
                content!, "Submit", ContentTransitionActor.ForSystem("lifecycle-test"), Trusted, token);
            moved.Succeeded.Should().BeTrue(string.Join("; ", moved.Errors));

            // The entry is still Draft to everyone else. An edit that counted now would find nothing in Submitted.
            edit = Task.Run(() => PutAsync(admin, type, DraftOnly()), Ct);
            await WaitForQueuedAsync(type);
            edit.IsCompleted.Should().BeFalse("the edit is queued behind the move's shared lock");
            (await LocksAsync(type, granted: false, "ExclusiveLock")).Should().Be(1, "the edit asks for the lock exclusive");
        });

        var blockers = await RefusedWithAsync(await edit!.WaitAsync(Limit, Ct));
        blockers.Should().ContainSingle().Which.Should().Be(("entryState", "Submitted", 1), "it counted after the move committed");
        (await StoredAsync(type)).Lifecycle!.States.Should().Equal("Draft", "Submitted");
    }

    [Fact]
    public async Task Edit_answers_409_when_the_lifecycle_changed_between_the_count_and_the_save()
    {
        var admin = await AdminAsync();
        var type = await TypeAsync(Claim());
        var changedBehindIt = new LifecycleDefinition { States = ["Draft", "Escalated"], InitialState = "Draft", Transitions = [] };

        barakoCMS.Features.ContentType.SetLifecycle.Endpoint.AfterCountForTests = async (counted, _) =>
        {
            if (counted.Name == type)
            {
                await ReplaceLifecycleAsync(type, changedBehindIt);
            }
        };
        HttpResponseMessage response;
        try
        {
            response = await PutAsync(admin, type, WithoutApproved());
        }
        finally
        {
            barakoCMS.Features.ContentType.SetLifecycle.Endpoint.AfterCountForTests = null;
        }

        response.StatusCode.Should().Be(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync(Ct));
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("changed while this ran");
        (await StoredAsync(type)).Lifecycle!.States.Should().Equal(new[] { "Draft", "Escalated" }, "the edit that won the race is what stays");
        (await AuditAsync(type)).Should().BeEmpty("nothing was written by the refused edit");
    }

    [Fact]
    public async Task Two_edits_from_the_same_version_save_the_first_and_refuse_the_second()
    {
        var admin = await AdminAsync();
        var type = await TypeAsync(Small());
        var version = await VersionOfAsync(type);
        var first = Small();
        first.States.Add("Approved");
        var second = Small();
        second.States.Add("Rejected");

        var responses = await Task.WhenAll(
            PutAsync(admin, type, first, version: version), PutAsync(admin, type, second, version: version));

        // The lock makes one run after the other, and the second one's version is then stale.
        responses.Select(r => r.StatusCode).Should().BeEquivalentTo(new[] { HttpStatusCode.OK, HttpStatusCode.PreconditionFailed });
        var winner = responses[0].StatusCode == HttpStatusCode.OK ? first : second;
        (await StoredAsync(type)).Lifecycle!.States.Should().Equal(winner.States, "the refused edit stored nothing");
        (await AuditAsync(type)).Should().HaveCount(1);
        var stale = responses.Single(r => r.StatusCode == HttpStatusCode.PreconditionFailed);
        var body = await BodyAsync(stale);
        body.GetProperty("status").GetInt32().Should().Be(412);
        body.GetProperty("lifecycle").GetProperty("states").EnumerateArray().Select(s => s.GetString())
            .Should().Equal(winner.States);
        body.GetProperty("version").GetGuid().Should().Be(await VersionOfAsync(type));
        (await StoredAsync(type)).Lifecycle!.States.Should().Equal(winner.States);
    }

    [Fact]
    public async Task Edit_without_a_version_is_refused_with_428_and_a_dry_run_needs_none()
    {
        var admin = await AdminAsync();
        var type = await TypeAsync(Small());

        var missing = await PutAsync(admin, type, Claim(), noVersion: true);
        var dry = await PutAsync(admin, type, Claim(), dryRun: true, noVersion: true);
        var garbled = new HttpRequestMessage(HttpMethod.Put, $"/api/content-types/{type}/lifecycle")
        {
            Content = JsonContent.Create(new { lifecycle = Claim() }),
        };
        garbled.Headers.TryAddWithoutValidation("If-Match", "not-a-version");
        var invalid = await admin.SendAsync(garbled, Ct);

        missing.StatusCode.Should().Be((HttpStatusCode)428, await missing.Content.ReadAsStringAsync(Ct));
        (await missing.Content.ReadAsStringAsync(Ct)).Should().Contain("If-Match is required");
        dry.StatusCode.Should().Be(HttpStatusCode.OK, await dry.Content.ReadAsStringAsync(Ct));
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await StoredAsync(type)).Lifecycle!.States.Should().Equal("Draft", "Submitted");
        (await AuditAsync(type)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_stale_dry_run_is_refused_like_a_stale_save()
    {
        var admin = await AdminAsync();
        var type = await TypeAsync(Small());
        var loaded = await VersionOfAsync(type);
        (await PutAsync(admin, type, Claim())).StatusCode.Should().Be(HttpStatusCode.OK);

        var dry = await PutAsync(admin, type, DraftOnly(), dryRun: true, version: loaded);
        var save = await PutAsync(admin, type, DraftOnly(), version: loaded);

        dry.StatusCode.Should().Be(HttpStatusCode.PreconditionFailed);
        save.StatusCode.Should().Be(HttpStatusCode.PreconditionFailed);
        (await BodyAsync(dry)).GetProperty("version").GetGuid().Should().Be(await VersionOfAsync(type));
        (await StoredAsync(type)).Lifecycle!.States.Should().Equal("Draft", "Review", "Approved");
        (await AuditAsync(type)).Should().HaveCount(1, "only the edit that was accepted");
    }

    [Fact]
    public async Task Edit_returns_the_new_version_in_the_etag_and_the_body_and_the_list_shows_it()
    {
        var slug = await TenantProfileSeed.TenantAsync(factory, Ct);
        var admin = await TenantProfileSeed.AdminInAsync(factory, slug, Ct);
        var type = await TypeAsync(Small(), slug);
        var before = await VersionOfAsync(type, slug);

        var first = await PutAsync(admin, type, Claim(), tenant: slug);

        first.StatusCode.Should().Be(HttpStatusCode.OK, await first.Content.ReadAsStringAsync(Ct));
        var after = await VersionOfAsync(type, slug);
        after.Should().NotBe(before);
        first.Headers.ETag!.Tag.Should().Be($"\"{after:D}\"");
        (await BodyAsync(first)).GetProperty("version").GetGuid().Should().Be(after);

        var list = await BodyAsync(await admin.GetAsync("/api/content-types?pageSize=100", Ct));
        list.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("name").GetString() == type)
            .GetProperty("version").GetGuid().Should().Be(after);

        // The version the answer carried is the one the next edit starts from.
        var next = Claim();
        next.States.Add("Closed");
        (await PutAsync(admin, type, next, version: after, tenant: slug)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Edit_with_dryRun_stores_nothing_and_reports_the_same_refusals_counts_and_references()
    {
        var admin = await AdminAsync();
        var type = await TypeAsync(Claim());
        await StoredEntryAsync(type, "Review");
        await StoredEntryAsync(type, "Review");
        var smuggled = await WarmTheCacheAndSmuggleATypeAsync();
        var key = "lc-" + Guid.NewGuid().ToString("n");

        var dry = await PutAsync(admin, type, DraftOnly(), dryRun: true, key);
        var real = await PutAsync(admin, type, DraftOnly());

        dry.StatusCode.Should().Be(HttpStatusCode.OK, await dry.Content.ReadAsStringAsync(Ct));
        var dryBody = await BodyAsync(dry);
        dryBody.GetProperty("dryRun").GetBoolean().Should().BeTrue();
        dryBody.GetProperty("canApply").GetBoolean().Should().BeFalse();
        var realBlockers = await RefusedWithAsync(real);
        Blockers(dryBody).Should().BeEquivalentTo(realBlockers, "the route and its dry run share one check");
        (await StoredAsync(type)).Lifecycle!.States.Should().Equal("Draft", "Review", "Approved");
        (await AuditAsync(type)).Should().BeEmpty();
        await AssertCacheIsStaleAsync(smuggled);

        var fine = Claim();
        fine.States.Add("Closed");
        var again = await PutAsync(admin, type, fine, dryRun: true, key);
        (await BodyAsync(again)).GetProperty("canApply").GetBoolean().Should().BeTrue();
        (await StoredAsync(type)).Lifecycle!.States.Should().Equal("Draft", "Review", "Approved");
        (await AuditAsync(type)).Should().BeEmpty();
        await AssertCacheIsStaleAsync(smuggled);

        var applied = await PutAsync(admin, type, fine, key: key);
        applied.StatusCode.Should().Be(HttpStatusCode.OK, "the dry run released the idempotency key: " + await applied.Content.ReadAsStringAsync(Ct));
        (await BodyAsync(applied)).GetProperty("dryRun").GetBoolean().Should().BeFalse();
        (await StoredAsync(type)).Lifecycle!.States.Should().Contain("Closed");
        (await AuditAsync(type)).Should().HaveCount(1);
    }

    [Fact]
    public async Task Dry_run_lists_every_reference_not_just_the_first()
    {
        var admin = await AdminAsync();
        var type = await TypeAsync(Claim());
        await StoredEntryAsync(type, "Review");
        await StoredEntryAsync(type, "Approved");
        await StoredEntryAsync(type, "Approved");
        await StoredEntryAsync(type, null);
        var elsewhere = new LifecycleDefinition { States = ["Escalated"], InitialState = "Escalated", Transitions = [] };

        var response = await PutAsync(admin, type, elsewhere, dryRun: true);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var body = await BodyAsync(response);
        body.GetProperty("canApply").GetBoolean().Should().BeFalse();
        Blockers(body).Should().BeEquivalentTo(new[]
        {
            ("entryState", (string?)"Draft", 1),
            ("entryState", (string?)"Review", 1),
            ("entryState", (string?)"Approved", 2),
            ("initialState", (string?)"Draft", 4),
        });
    }

    // ---- what else names the state or transition an edit takes away ---------------------------

    /// <summary>Dry-runs the claim lifecycle without Approved and Approve, and returns what it found of one kind.</summary>
    private async Task<(JsonElement Body, List<JsonElement> Found)> DryRunWithoutApprovedAsync(string type, string kind)
    {
        var response = await PutAsync(await AdminAsync(), type, WithoutApproved(), dryRun: true);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var body = await BodyAsync(response);
        var found = body.GetProperty("blockers").EnumerateArray().Concat(body.GetProperty("warnings").EnumerateArray())
            .Where(b => Str(b, "kind") == kind).ToList();
        return (body, found);
    }

    private async Task StoreAsync(params object[] documents)
    {
        await using var session = SessionIn(null);
        foreach (var document in documents)
        {
            session.Store(document);
        }

        await session.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Edit_refuses_removing_a_transition_a_role_grants()
    {
        var type = await TypeAsync(Claim());
        var role = new Role { Id = Guid.NewGuid(), Name = NewName("role"), Permissions = [new ContentTypePermission
        {
            ContentTypeSlug = type,
            Transitions = new() { ["approve"] = new PermissionRule { Enabled = true } },
        }] };
        await StoreAsync(role);

        var (body, found) = await DryRunWithoutApprovedAsync(type, "role");

        found.Should().ContainSingle().Which.GetProperty("name").GetString().Should().Be(role.Name);
        Str(found[0], "transition").Should().Be("Approve");
        body.GetProperty("canApply").GetBoolean().Should().BeFalse();
        var refused = await PutAsync(await AdminAsync(), type, WithoutApproved());
        (await RefusedWithAsync(refused)).Should().Contain(b => b.Kind == "role");
    }

    [Fact]
    public async Task Edit_ignores_a_role_that_grants_a_transition_the_edit_keeps_or_on_another_type()
    {
        var type = await TypeAsync(Claim());
        await StoreAsync(new Role { Id = Guid.NewGuid(), Name = NewName("role"), Permissions =
        [
            new ContentTypePermission { ContentTypeSlug = type, Transitions = new() { ["Submit"] = new PermissionRule { Enabled = true } } },
            new ContentTypePermission { ContentTypeSlug = "other-" + type, Transitions = new() { ["Approve"] = new PermissionRule { Enabled = true } } },
        ] });

        (await DryRunWithoutApprovedAsync(type, "role")).Found.Should().BeEmpty();
    }

    [Fact]
    public async Task Edit_refuses_removing_a_transition_a_workflow_is_triggered_by()
    {
        var type = await TypeAsync(Claim());
        var workflow = new WorkflowDefinition
        {
            Id = Guid.NewGuid(), Name = NewName("wf"), TriggerContentType = type, TriggerEvent = "transition:Approve", Enabled = true,
        };
        await StoreAsync(workflow);

        var (_, found) = await DryRunWithoutApprovedAsync(type, "workflowTrigger");

        found.Should().ContainSingle().Which.GetProperty("name").GetString().Should().Be(workflow.Name);
        var refused = await PutAsync(await AdminAsync(), type, WithoutApproved());
        (await RefusedWithAsync(refused)).Should().Contain(b => b.Kind == "workflowTrigger");
    }

    [Fact]
    public async Task Edit_refuses_renaming_a_transition_only_in_case_when_a_workflow_is_triggered_by_it()
    {
        var type = await TypeAsync(Claim());
        var workflow = new WorkflowDefinition
        {
            Id = Guid.NewGuid(), Name = NewName("wf"), TriggerContentType = type, TriggerEvent = "transition:Approve", Enabled = true,
        };
        await StoreAsync(workflow);
        var edited = Claim();
        edited.Transitions[1].Name = "ApproVe";

        var dry = await BodyAsync(await PutAsync(await AdminAsync(), type, edited, dryRun: true));
        var refused = await PutAsync(await AdminAsync(), type, edited);

        dry.GetProperty("blockers").EnumerateArray().Where(b => Str(b, "kind") == "workflowTrigger").Should()
            .ContainSingle().Which.GetProperty("name").GetString().Should().Be(workflow.Name);
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await StoredAsync(type)).Lifecycle!.Transitions.Select(t => t.Name).Should().Contain("Approve");
    }

    [Fact]
    public async Task Edit_refuses_removing_a_transition_a_workflow_action_makes_even_nested_or_undoing()
    {
        var type = await TypeAsync(Claim());
        var nested = new WorkflowAction { Type = "Transition", Parameters = new() { ["ContentType"] = type, ["Transition"] = "approve" } };
        var holder = new WorkflowDefinition
        {
            Id = Guid.NewGuid(), Name = NewName("wf"), TriggerContentType = "unrelated", TriggerEvent = "Created", Enabled = true,
            Actions =
            [
                new WorkflowAction
                {
                    Type = "Conditional",
                    Parameters = new() { ["ThenActions"] = System.Text.Json.JsonSerializer.Serialize(new[] { nested }) },
                },
            ],
        };
        var undoing = new WorkflowDefinition
        {
            Id = Guid.NewGuid(), Name = NewName("wf"), TriggerContentType = "unrelated", TriggerEvent = "Created", Enabled = true,
            Actions =
            [
                new WorkflowAction
                {
                    Type = "Log",
                    Compensate = new WorkflowCompensation { Type = "Transition", Parameters = new() { ["ContentType"] = type, ["Transition"] = "Approve" } },
                },
            ],
        };
        var elsewhere = new WorkflowDefinition
        {
            Id = Guid.NewGuid(), Name = NewName("wf"), TriggerContentType = "unrelated", TriggerEvent = "Created", Enabled = true,
            Actions = [new WorkflowAction { Type = "Transition", Parameters = new() { ["ContentType"] = "other-" + type, ["Transition"] = "Approve" } }],
        };
        await StoreAsync(holder, undoing, elsewhere);

        var (_, found) = await DryRunWithoutApprovedAsync(type, "workflowAction");

        found.Select(b => Str(b, "name")).Should().BeEquivalentTo(holder.Name, undoing.Name);
    }

    [Fact]
    public async Task Edit_refuses_removing_a_transition_an_unfinished_run_carries_and_counts_runs()
    {
        var type = await TypeAsync(Claim());
        var workflow = NewName("wf");
        WorkflowRun Run(RunStatus status, string trigger, string content) => new()
        {
            Id = Guid.NewGuid(), WorkflowName = workflow, ContentType = content, TriggerEvent = trigger, Status = status,
        };
        await StoreAsync(
            Run(RunStatus.Pending, "transition:Approve", type),
            Run(RunStatus.Running, "transition:approve", type),
            Run(RunStatus.Succeeded, "transition:Approve", type),
            Run(RunStatus.Pending, "transition:Approve", "other-" + type),
            Run(RunStatus.Pending, "transition:Submit", type));

        var (_, found) = await DryRunWithoutApprovedAsync(type, "workflowRun");

        found.Should().ContainSingle();
        CountOf(found[0]).Should().Be(2);
        Str(found[0], "name").Should().Be(workflow);
    }

    [Fact]
    public async Task Edit_refuses_removing_a_transition_a_run_is_still_undoing()
    {
        var type = await TypeAsync(Claim());
        var workflow = NewName("wf");
        WorkflowRun Run(RunStatus status, RunCompensation undoing, CompensationStatus step) => new()
        {
            Id = Guid.NewGuid(), WorkflowName = workflow, ContentType = type, TriggerEvent = "Created", Status = status,
            Compensation = undoing,
            Actions =
            [
                new WorkflowActionAttempt
                {
                    ActionType = "Log", Status = AttemptStatus.Succeeded,
                    Compensate = new WorkflowCompensation { Type = "Transition", Parameters = new() { ["ContentType"] = type, ["Transition"] = "Approve" } },
                    Compensation = new CompensationState { Status = step },
                },
            ],
        };
        await StoreAsync(
            Run(RunStatus.Failed, RunCompensation.Compensating, CompensationStatus.Pending),
            Run(RunStatus.Failed, RunCompensation.Compensated, CompensationStatus.Succeeded));

        var (_, found) = await DryRunWithoutApprovedAsync(type, "workflowRun");

        found.Should().ContainSingle();
        CountOf(found[0]).Should().Be(1, "only the run still undoing counts");
        Str(found[0], "transition").Should().Be("Approve");
    }

    [Fact]
    public async Task Edit_refuses_removing_a_state_a_rule_names_on_the_type_or_from_another_type()
    {
        var type = await TypeAsync(Claim());
        var reader = new ContentTypeDefinition
        {
            Id = Guid.NewGuid(), Name = NewName("reader"), DisplayName = "Reader",
            Rules =
            [
                new RuleDefinition
                {
                    Name = "reads-the-other-type",
                    If = [new RuleCondition { Kind = "exists", ContentType = type, State = "Review,Approved" }],
                },
                new RuleDefinition
                {
                    Name = "reads-an-unrelated-type",
                    If = [new RuleCondition { Kind = "exists", ContentType = "unrelated", State = "Approved" }],
                },
            ],
        };
        await StoreAsync(reader);
        await using (var session = SessionIn(null))
        {
            var stored = await session.Query<ContentTypeDefinition>().SingleAsync(d => d.Name == type, Ct);
            stored.Rules = [new RuleDefinition { Name = "own-rule", InState = "Approved" }, new RuleDefinition { Name = "own-move", When = RuleWhen.Transition, Transition = "Approve" }];
            session.Store(stored);
            await session.SaveChangesAsync(Ct);
        }

        var (_, found) = await DryRunWithoutApprovedAsync(type, "rule");

        found.Select(b => Str(b, "name")).Should().BeEquivalentTo("own-rule", "own-move", "reads-the-other-type");
    }

    [Fact]
    public async Task Edit_refuses_removing_a_state_a_uniqueness_rule_applies_in()
    {
        var type = await TypeAsync(Claim());
        await using (var session = SessionIn(null))
        {
            var stored = await session.Query<ContentTypeDefinition>().SingleAsync(d => d.Name == type, Ct);
            stored.Uniqueness =
            [
                new UniquenessRule { Name = "one-approved", Fields = ["Title"], WhenState = "approved" },
                new UniquenessRule { Name = "one-draft", Fields = ["Title"], WhenState = "Draft" },
            ];
            session.Store(stored);
            await session.SaveChangesAsync(Ct);
        }

        var (_, found) = await DryRunWithoutApprovedAsync(type, "uniqueness");

        found.Should().ContainSingle().Which.GetProperty("name").GetString().Should().Be("one-approved");
        var refused = await PutAsync(await AdminAsync(), type, WithoutApproved());
        (await RefusedWithAsync(refused)).Should().Contain(b => b.Kind == "uniqueness");
    }

    [Fact]
    public async Task Edit_refuses_removing_a_state_a_date_schedule_sweeps()
    {
        var type = await TypeAsync(Claim());
        var swept = new WorkflowDefinition
        {
            Id = Guid.NewGuid(), Name = NewName("wf"), TriggerContentType = type, TriggerEvent = "Schedule", Enabled = true,
            DateSchedule = new ScheduleTrigger { ContentType = type, DateField = "Due", InState = "Approved" },
        };
        var kept = new WorkflowDefinition
        {
            Id = Guid.NewGuid(), Name = NewName("wf"), TriggerContentType = type, TriggerEvent = "Schedule", Enabled = true,
            DateSchedule = new ScheduleTrigger { ContentType = type, DateField = "Due", InState = "Draft" },
        };
        await StoreAsync(swept, kept);

        var (_, found) = await DryRunWithoutApprovedAsync(type, "schedule");

        found.Should().ContainSingle().Which.GetProperty("name").GetString().Should().Be(swept.Name);
        Str(found[0], "state").Should().Be("Approved");
    }

    [Fact]
    public async Task Edit_refuses_renaming_a_state_only_in_case_when_a_schedule_sweeps_it()
    {
        var type = await TypeAsync(Claim());
        var swept = new WorkflowDefinition
        {
            Id = Guid.NewGuid(), Name = NewName("wf"), TriggerContentType = type, TriggerEvent = "Schedule", Enabled = true,
            DateSchedule = new ScheduleTrigger { ContentType = type, DateField = "Due", InState = "Approved" },
        };
        await StoreAsync(swept);
        var edited = Claim();
        edited.States[2] = "ApprovEd";
        edited.Transitions[1].To = "ApprovEd";

        var dry = await BodyAsync(await PutAsync(await AdminAsync(), type, edited, dryRun: true));
        var refused = await PutAsync(await AdminAsync(), type, edited);

        dry.GetProperty("blockers").EnumerateArray().Where(b => Str(b, "kind") == "schedule").Should()
            .ContainSingle().Which.GetProperty("name").GetString().Should().Be(swept.Name);
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Edit_warns_about_an_email_link_to_a_removed_transition_and_still_applies()
    {
        var type = await TypeAsync(Claim());
        var workflow = new WorkflowDefinition
        {
            Id = Guid.NewGuid(), Name = NewName("wf"), TriggerContentType = type, TriggerEvent = "Updated", Enabled = true,
            Actions =
            [
                new WorkflowAction
                {
                    Type = "Email",
                    Parameters = new() { ["Body"] = "Please {{links.transition \"approve\"}} or {{links.transition \"Submit\"}}" },
                },
            ],
        };
        await StoreAsync(workflow);

        var response = await PutAsync(await AdminAsync(), type, WithoutApproved(), dryRun: true);
        var body = await BodyAsync(response);

        body.GetProperty("canApply").GetBoolean().Should().BeTrue("a link in a template only warns");
        var warnings = body.GetProperty("warnings").EnumerateArray().Where(b => Str(b, "kind") == "templateLink").ToList();
        warnings.Should().ContainSingle().Which.GetProperty("transition").GetString().Should().Be("Approve");
        (await PutAsync(await AdminAsync(), type, WithoutApproved())).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ---- gate, audit, contract ---------------------------------------------------------------

    [Fact]
    public async Task Edit_needs_ManageContentTypes()
    {
        var type = await TypeAsync(Small());
        var anonymous = factory.CreateClient();
        var (editor, _) = await CallerHoldingAsync();
        var (author, _) = await CallerHoldingAsync(SystemCapabilities.ManageContentTypes);

        (await PutAsync(anonymous, type, Claim())).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await PutAsync(editor, type, Claim())).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await StoredAsync(type)).Lifecycle!.States.Should().Equal("Draft", "Submitted");

        (await PutAsync(author, type, Claim())).StatusCode.Should().Be(HttpStatusCode.OK, "the capability alone opens it");
    }

    private async Task<(HttpClient Client, Guid RoleId)> CallerHoldingAsync(params string[] capabilities)
    {
        var unique = $"Editor {Guid.NewGuid():N}";
        using var scope = factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var role = new Role { Id = Guid.NewGuid(), Name = unique, SystemCapabilities = capabilities.ToList() };
        session.Store(role);
        var userId = Guid.NewGuid();
        session.Store(new User
        {
            Id = userId,
            Username = $"lc-{userId}",
            Email = $"lc-{userId}@example.com",
            RoleIds = [role.Id],
        });
        await session.SaveChangesAsync(Ct);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", factory.CreateToken(roles: [unique], userId: userId.ToString()));
        return (client, role.Id);
    }

    [Fact]
    public async Task Edit_writes_an_audit_row_contenttype_lifecycle_changed_with_before_and_after_and_who()
    {
        var (admin, user) = await AdminWithUserAsync();
        var type = await TypeAsync(Small());
        var other = await TypeAsync(Small());

        (await PutAsync(admin, type, Claim())).StatusCode.Should().Be(HttpStatusCode.OK);
        (await PutAsync(admin, other, Small())).StatusCode.Should().Be(HttpStatusCode.OK, "a no-op");

        var row = (await AuditAsync(type)).Should().ContainSingle().Subject;
        row.ActorUserId.Should().Be(user.Id);
        row.ActorUsername.Should().Be(user.Username);
        row.TargetType.Should().Be("ContentType");
        row.Metadata!.Keys.Should().BeEquivalentTo("contentType", "from", "to");
        row.Metadata["contentType"].ToString().Should().Be(type);
        // Stored as the lifecycle itself, a nested object, so it is read as JSON.
        JsonSerializer.Serialize(row.Metadata["from"]).Should().Contain("Submitted").And.NotContain("Review");
        JsonSerializer.Serialize(row.Metadata["to"]).Should().Contain("Review").And.Contain("Approve");
        (await AuditAsync(other)).Should().BeEmpty("a lifecycle the type already has changes nothing and records nothing");
    }

    [Fact]
    public async Task Edit_invalidates_the_openapi_cache()
    {
        var admin = await AdminAsync();
        var type = await TypeAsync(Small());
        var smuggled = await WarmTheCacheAndSmuggleATypeAsync();

        (await PutAsync(admin, type, Claim())).StatusCode.Should().Be(HttpStatusCode.OK);

        using var fresh = await OpenApiTagTests.FetchDocumentAsync(factory);
        fresh.RootElement.GetProperty("paths").TryGetProperty($"/api/public/{smuggled}", out _)
            .Should().BeTrue("the edit dropped the cached document, so it was built again");
    }

    [Theory]
    [InlineData("""{"lifecycle":{"states":null,"initialState":"Draft","transitions":[]}}""")]
    [InlineData("""{"lifecycle":{"states":["Draft"],"initialState":"Draft","transitions":null}}""")]
    [InlineData("""{"lifecycle":{"states":["Draft"],"initialState":"Draft","transitions":[null]}}""")]
    [InlineData("""{"lifecycle":{"states":[null],"initialState":"Draft","transitions":[]}}""")]
    [InlineData("""{"lifecycle":{"states":["Draft","Review"],"initialState":"Draft","transitions":[{"name":null,"from":"Draft","to":"Review"}]}}""")]
    [InlineData("""{"lifecycle":{"states":["Draft","Review"],"initialState":"Draft","transitions":[{"name":"Submit","from":null,"to":"Review"}]}}""")]
    [InlineData("""{"lifecycle":{"states":["Draft","Review"],"initialState":"Draft","transitions":[{"name":"Submit","from":"Draft","to":null}]}}""")]
    public async Task Edit_with_a_null_list_or_null_item_is_400_not_500(string body)
    {
        var admin = await AdminAsync();
        var type = await TypeAsync(Small());

        var response = await RawPutAsync(admin, type, body, await VersionOfAsync(type));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync(Ct));
        (await StoredAsync(type)).Lifecycle!.States.Should().Equal("Draft", "Submitted");
        (await AuditAsync(type)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("""{"lifecycle":null}""", false, true)]
    [InlineData("""{"lifecycle":null}""", false, false)]
    [InlineData("""{"lifecycle":null,"dryRun":true}""", true, true)]
    [InlineData("""{"lifecycle":null,"dryRun":true}""", true, false)]
    [InlineData("""{}""", false, true)]
    [InlineData("""{}""", false, false)]
    [InlineData("""{"dryRun":true}""", true, true)]
    [InlineData("""{"dryRun":true}""", true, false)]
    public async Task Edit_without_a_lifecycle_is_400_before_the_version_is_looked_at(string body, bool dryRun, bool sendVersion)
    {
        _ = dryRun;
        var admin = await AdminAsync();
        var type = await TypeAsync(Small());

        var response = await RawPutAsync(admin, type, body, sendVersion ? await VersionOfAsync(type) : null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync(Ct));
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("lifecycle is required");
        (await StoredAsync(type)).Lifecycle!.States.Should().Equal("Draft", "Submitted");
        (await AuditAsync(type)).Should().BeEmpty();
    }

    private async Task<HttpResponseMessage> RawPutAsync(HttpClient client, string type, string json, Guid? version)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/content-types/{type}/lifecycle")
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        if (version is { } v)
        {
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{v:D}\"");
        }

        return await client.SendAsync(request, Ct);
    }

    [Fact]
    public async Task Edit_then_config_export_then_apply_is_a_no_op()
    {
        var slug = await TenantProfileSeed.TenantAsync(factory, Ct);
        var admin = await TenantProfileSeed.AdminInAsync(factory, slug, Ct);
        var type = await TypeAsync(Small(), slug);
        (await PutAsync(admin, type, Claim(), tenant: slug)).StatusCode.Should().Be(HttpStatusCode.OK);

        var export = await admin.GetAsync("/api/config/export?sections=contentTypes", Ct);
        export.StatusCode.Should().Be(HttpStatusCode.OK);
        var document = JsonNode.Parse(await export.Content.ReadAsStringAsync(Ct))!;

        var plan = await admin.PostAsJsonAsync("/api/config/plan", new { document }, Ct);
        var applied = await admin.PostAsJsonAsync("/api/config/apply", new { document }, Ct);

        var planned = await plan.Content.ReadAsStringAsync(Ct);
        plan.StatusCode.Should().Be(HttpStatusCode.OK, planned);
        var sections = JsonDocument.Parse(planned).RootElement.GetProperty("sections").EnumerateArray().ToList();
        sections.Should().NotBeEmpty("an empty plan would pass the check below for the wrong reason");
        sections.SelectMany(s => s.GetProperty("changes").EnumerateArray()).Should().BeEmpty();
        var exported = document["contentTypes"]!.AsObject();
        exported.Select(p => p.Key).Should().Contain(type, "the export has to carry the edited type");
        var lifecycle = exported[type]!.AsObject()
            .First(p => p.Key.Equals("lifecycle", StringComparison.OrdinalIgnoreCase)).Value!.AsObject();
        lifecycle.First(p => p.Key.Equals("states", StringComparison.OrdinalIgnoreCase)).Value!.AsArray()
            .Select(s => s!.GetValue<string>()).Should().Equal("Draft", "Review", "Approved");
        applied.StatusCode.Should().Be(HttpStatusCode.OK, await applied.Content.ReadAsStringAsync(Ct));
        (await StoredAsync(type, slug)).Lifecycle!.States.Should().Equal("Draft", "Review", "Approved");
    }

    [Fact]
    public async Task Config_apply_and_bundle_import_still_refuse_a_changed_lifecycle_and_the_text_names_the_new_route()
    {
        var slug = await TenantProfileSeed.TenantAsync(factory, Ct);
        var admin = await TenantProfileSeed.AdminInAsync(factory, slug, Ct);
        var type = await TypeAsync(Claim(), slug);
        var route = $"PUT /api/content-types/{type}/lifecycle";

        var export = await admin.GetAsync("/api/config/export?sections=contentTypes", Ct);
        var document = JsonNode.Parse(await export.Content.ReadAsStringAsync(Ct))!;
        AddState(document["contentTypes"]![type]!);
        var apply = await admin.PostAsJsonAsync("/api/config/apply", new { document, allowDestructive = true }, Ct);

        var exported = await admin.GetAsync($"/api/portability/export?types={type}", Ct);
        var bundle = (await exported.Content.ReadFromJsonAsync<PortabilityBundle>(ApiJson.Options, Ct))!;
        bundle.ContentTypes[0].Lifecycle!.States.Add("Extra");
        var import = await admin.PostAsJsonAsync(
            "/api/portability/import", new { contentTypes = bundle.ContentTypes, contents = bundle.Contents }, ApiJson.Options, Ct);

        apply.IsSuccessStatusCode.Should().BeFalse();
        (await apply.Content.ReadAsStringAsync(Ct)).Should().Contain(route);
        import.IsSuccessStatusCode.Should().BeFalse();
        (await import.Content.ReadAsStringAsync(Ct)).Should().Contain(route);
        (await StoredAsync(type, slug)).Lifecycle!.States.Should().Equal("Draft", "Review", "Approved");
    }

    private static void AddState(JsonNode contentType)
    {
        var lifecycle = contentType.AsObject().First(p => p.Key.Equals("lifecycle", StringComparison.OrdinalIgnoreCase)).Value!;
        lifecycle.AsObject().First(p => p.Key.Equals("states", StringComparison.OrdinalIgnoreCase)).Value!.AsArray().Add("Extra");
    }

    [Fact]
    public async Task Public_delivery_output_is_unchanged_after_a_lifecycle_edit()
    {
        var admin = await AdminAsync();
        var type = await TypeAsync(Small(), deliverable: true);
        await StoredEntryAsync(type, "Submitted", status: ContentStatus.Published);
        var anonymous = factory.CreateClient();
        var before = await anonymous.GetAsync($"/api/public/{type}", Ct);
        before.StatusCode.Should().Be(HttpStatusCode.OK);
        var beforeText = await before.Content.ReadAsStringAsync(Ct);

        var grown = Small();
        grown.States.Add("Approved");
        (await PutAsync(admin, type, grown)).StatusCode.Should().Be(HttpStatusCode.OK);

        var after = await anonymous.GetAsync($"/api/public/{type}", Ct);
        (await after.Content.ReadAsStringAsync(Ct)).Should().Be(beforeText, "delivery serves the publication status, not the lifecycle state");
    }

    [Fact]
    public async Task Api_contract_versions_are_unchanged()
    {
        var admin = await AdminAsync();
        var type = await TypeAsync(Small());

        var response = await PutAsync(admin, type, Claim());

        ApiContract.Version.Should().Be(16);
        ApiContract.DeliveryVersion.Should().Be(10);
        response.Headers.GetValues(ApiContract.HeaderName).Should().Equal("16");
        response.Headers.GetValues(ApiContract.DeliveryHeaderName).Should().Equal("10");
    }

    // ---- cache and lock helpers --------------------------------------------------------------

    /// <summary>Warms the delivery document, then stores a deliverable type straight in, so only an invalidation shows it.</summary>
    private async Task<string> WarmTheCacheAndSmuggleATypeAsync()
    {
        using (var _ = await OpenApiTagTests.FetchDocumentAsync(factory))
        {
        }

        return await TypeAsync(null, deliverable: true);
    }

    private async Task AssertCacheIsStaleAsync(string smuggled)
    {
        using var document = await OpenApiTagTests.FetchDocumentAsync(factory);
        document.RootElement.GetProperty("paths").TryGetProperty($"/api/public/{smuggled}", out _)
            .Should().BeFalse("nothing invalidated the cached document");
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

    /// <summary>Returns once a session is queued for the type's lock. Bounded, and the only wait in this class.</summary>
    private async Task WaitForQueuedAsync(string type)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        limit.CancelAfter(Limit);
        while (await LocksAsync(type, granted: false) < 1)
        {
            limit.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }
}
