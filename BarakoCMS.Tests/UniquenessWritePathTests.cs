using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Core.Interfaces;
using barakoCMS.Events;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Infrastructure.Services;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// A type's uniqueness rules hold on the writes that do not come through the create and update
/// endpoints, and two writes of one value at once are serialised by the lock, not by luck.
/// </summary>
/// <remarks>
/// Each test declares its own type, and the ones that write through a session of their own use a
/// tenant of their own, so nothing here reads another test's entries.
/// </remarks>
[Collection("Sequential")]
public class UniquenessWritePathTests
{
    private readonly IntegrationTestFixture _fixture;

    public UniquenessWritePathTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string BadgeRule = "OneHolderPerBadge";

    private const string OpenRule = "OneOpenEntryPerBadge";

    private static readonly ContentTransitionOptions Trusted = new() { SkipPermissionChecks = true };

    private static string NewType() => $"uwp{Guid.NewGuid():n}"[..14];

    private static LifecycleDefinition Clock() => new()
    {
        States = ["Open", "Closed"],
        InitialState = "Open",
        Transitions =
        [
            new StateTransition { Name = "ClockOut", From = "Open", To = "Closed" },
            new StateTransition { Name = "Reopen", From = "Closed", To = "Open" },
        ],
    };

    private static ContentTypeDefinition Badges(string name, LifecycleDefinition? lifecycle = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        DisplayName = "Badge",
        Fields = [new FieldDefinition { Name = "Badge", DisplayName = "Badge", Type = "string" }],
        Lifecycle = lifecycle,
        Uniqueness = lifecycle is null
            ? [new UniquenessRule { Name = BadgeRule, Fields = ["Badge"] }]
            : [new UniquenessRule { Name = OpenRule, Fields = ["Badge"], WhenState = "Open" }],
    };

    private static ContentCreated Created(string type, string badge, Guid? id = null) => new(
        id ?? Guid.NewGuid(), type, new Dictionary<string, object> { ["Badge"] = badge },
        ContentStatus.Draft, Guid.NewGuid(), null, SensitivityLevel.Public, DateTime.UtcNow);

    private static async Task StoreTypeAsync(IDocumentStore store, string tenant, ContentTypeDefinition type)
    {
        await using var session = store.LightweightSession(tenant);
        session.Store(type);
        await session.SaveChangesAsync(Ct);
    }

    private static async Task<int> CountAsync(IDocumentStore store, string tenant, string type)
    {
        await using var session = store.QuerySession(tenant);
        return await session.Query<Content>().CountAsync(c => c.ContentType == type, Ct);
    }

    // ---- the lock ---------------------------------------------------------------------------------

    /// <summary>
    /// The first write holds the lock and has not committed, so the second cannot see it in the
    /// database. Only the lock can refuse the second, and it does so as in progress once the wait
    /// runs out. Without the lock the second write finds no holder and is staged. Deterministic: it
    /// does not depend on how long either write takes.
    /// </summary>
    [Fact]
    public async Task A_write_of_a_value_another_uncommitted_write_holds_is_refused_as_in_progress()
    {
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var tenant = $"unl-{Guid.NewGuid():N}"[..14];
        var type = NewType();
        await StoreTypeAsync(store, tenant, Badges(type));

        await using var first = store.LightweightSession(tenant);
        await new ContentWriter(first, new ContentSourcingPolicyService(first)).CreateAsync(Created(type, "B-7"), Ct);

        await using (var second = store.LightweightSession(tenant))
        {
            var secondWriter = new ContentWriter(second, new ContentSourcingPolicyService(second));
            var write = async () => await secondWriter.CreateAsync(Created(type, "B-7"), Ct);

            var refused = await write.Should().ThrowAsync<ContentUniquenessException>();
            refused.Which.IsInProgress.Should().BeTrue("the first write had not committed, so only its lock can stand in the way");
            refused.Which.Message.Should().Contain("Try again");
        }

        await first.SaveChangesAsync(Ct);
        (await CountAsync(store, tenant, type)).Should().Be(1);
    }

    [Fact]
    public async Task A_write_of_a_value_a_committed_entry_holds_is_refused_as_held()
    {
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var tenant = $"unl-{Guid.NewGuid():N}"[..14];
        var type = NewType();
        await StoreTypeAsync(store, tenant, Badges(type));

        await using (var first = store.LightweightSession(tenant))
        {
            await new ContentWriter(first, new ContentSourcingPolicyService(first)).CreateAsync(Created(type, "B-6"), Ct);
            await first.SaveChangesAsync(Ct);
        }

        await using var second = store.LightweightSession(tenant);
        var write = async () => await new ContentWriter(second, new ContentSourcingPolicyService(second))
            .CreateAsync(Created(type, "B-6"), Ct);

        var refused = await write.Should().ThrowAsync<ContentUniquenessException>();
        refused.Which.IsInProgress.Should().BeFalse();
        refused.Which.Rule.Should().Be(BadgeRule);
    }

    /// <summary>
    /// A writer that commits more than once forgets, at each commit, what it wrote: another request
    /// may move one of those entries afterwards, and only the database knows.
    /// </summary>
    [Fact]
    public async Task A_writer_that_committed_still_sees_a_value_another_request_moved_onto_one_of_its_entries()
    {
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var tenant = $"unl-{Guid.NewGuid():N}"[..14];
        var type = NewType();
        await StoreTypeAsync(store, tenant, Badges(type));

        await using var longLived = store.LightweightSession(tenant);
        var writer = new ContentWriter(longLived, new ContentSourcingPolicyService(longLived));
        var moved = await writer.CreateAsync(Created(type, "Y-1"), Ct);
        await longLived.SaveChangesAsync(Ct);

        await using (var other = store.LightweightSession(tenant))
        {
            var entry = await other.LoadAsync<Content>(moved.Id, Ct);
            await new ContentWriter(other, new ContentSourcingPolicyService(other)).AppendAsync(
                entry!, new ContentUpdated(entry!.Id, new Dictionary<string, object> { ["Badge"] = "X-1" }, Guid.NewGuid(), null, DateTime.UtcNow), Ct);
            await other.SaveChangesAsync(Ct);
        }

        var write = async () => await writer.CreateAsync(Created(type, "X-1"), Ct);

        await write.Should().ThrowAsync<ContentUniquenessException>();
    }

    /// <summary>
    /// A forced rule left two entries sharing a value. A sweep publishing both rewrites each without
    /// changing what it holds, and neither is refused over the other, though both go through one
    /// writer.
    /// </summary>
    [Fact]
    public async Task A_sweep_publishes_two_entries_that_share_a_value_under_a_forced_rule()
    {
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var tenant = $"uns-{Guid.NewGuid():N}"[..14];
        var type = NewType();
        await StoreTypeAsync(store, tenant, Badges(type));

        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        await using (var seed = store.LightweightSession(tenant))
        {
            foreach (var id in ids)
            {
                seed.Store(new Content
                {
                    Id = id,
                    ContentType = type,
                    Status = ContentStatus.Draft,
                    ScheduledPublishAt = DateTime.UtcNow.AddMinutes(-5),
                    Data = new() { ["Badge"] = "S-1" },
                });
            }

            await seed.SaveChangesAsync(Ct);
        }

        await using (var sweep = store.LightweightSession(tenant))
        {
            var applied = await ScheduledContentService.SweepTenantAsync(sweep, DateTime.UtcNow, Ct);
            applied.Should().Be(2);
        }

        await using var check = store.QuerySession(tenant);
        var stored = await check.LoadManyAsync<Content>(Ct, ids);
        stored.Should().HaveCount(2);
        stored.Should().OnlyContain(c => c.Status == ContentStatus.Published);
    }

    [Fact]
    public async Task A_write_of_another_value_does_not_wait_on_the_lock()
    {
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var tenant = $"unl-{Guid.NewGuid():N}"[..14];
        var type = NewType();
        await StoreTypeAsync(store, tenant, Badges(type));

        await using var first = store.LightweightSession(tenant);
        await new ContentWriter(first, new ContentSourcingPolicyService(first)).CreateAsync(Created(type, "B-7"), Ct);

        await using (var second = store.LightweightSession(tenant))
        {
            await new ContentWriter(second, new ContentSourcingPolicyService(second))
                .CreateAsync(Created(type, "B-8"), Ct)
                .WaitAsync(TimeSpan.FromSeconds(10), Ct);
            await second.SaveChangesAsync(Ct);
        }

        await first.SaveChangesAsync(Ct);
        (await CountAsync(store, tenant, type)).Should().Be(2);
    }

    [Fact]
    public async Task Two_creates_of_one_value_in_one_session_are_refused_before_either_is_stored()
    {
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var tenant = $"unl-{Guid.NewGuid():N}"[..14];
        var type = NewType();
        await StoreTypeAsync(store, tenant, Badges(type));

        await using var session = store.LightweightSession(tenant);
        var writer = new ContentWriter(session, new ContentSourcingPolicyService(session));
        await writer.CreateAsync(Created(type, "B-9"), Ct);

        var again = async () => await writer.CreateAsync(Created(type, "B-9"), Ct);
        await again.Should().ThrowAsync<ContentUniquenessException>();

        await session.SaveChangesAsync(Ct);
        (await CountAsync(store, tenant, type)).Should().Be(1, "the first create was staged and is kept");
    }

    // ---- a transition made from code --------------------------------------------------------------

    [Fact]
    public async Task A_transition_from_code_into_a_state_where_another_entry_holds_the_value_is_a_conflict()
    {
        var type = NewType();
        Guid monday;
        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(Badges(type, Clock()));
            await session.SaveChangesAsync(Ct);

            var created = await scope.ServiceProvider.GetRequiredService<IContentWriter>()
                .CreateAsync(Created(type, "B-1"), Ct);
            monday = created.Id;
            await session.SaveChangesAsync(Ct);
        }

        (await MoveAsync(monday, "ClockOut")).Outcome.Should().Be(ContentTransitionOutcome.Transitioned);

        using (var scope = _fixture.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IContentWriter>().CreateAsync(Created(type, "B-1"), Ct);
            await scope.ServiceProvider.GetRequiredService<IDocumentSession>().SaveChangesAsync(Ct);
        }

        var reopen = await MoveAsync(monday, "Reopen");

        reopen.Outcome.Should().Be(ContentTransitionOutcome.Conflict, string.Join(" ", reopen.Errors));
        reopen.Errors.Should().HaveCount(1);
        reopen.Errors[0].Should().Contain(OpenRule);

        using var check = _fixture.Services.CreateScope();
        var stored = await check.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<Content>(monday, Ct);
        stored!.LifecycleState.Should().Be("Closed");
    }

    private async Task<ContentTransitionResult> MoveAsync(Guid id, string transition)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var content = await session.LoadAsync<Content>(id, Ct);
        content.Should().NotBeNull();

        return await scope.ServiceProvider.GetRequiredService<IContentTransitioner>().TransitionAsync(
            content!, transition, ContentTransitionActor.ForSystem("uniqueness-test"), Trusted, Ct);
    }

    // ---- a rollback -------------------------------------------------------------------------------

    [Fact]
    public async Task A_rollback_to_a_value_another_entry_now_holds_is_refused()
    {
        var (token, _) = await TestHelpers.CreateAdminUserAsync(_fixture);
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var type = NewType();
        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(Badges(type));
            await session.SaveChangesAsync(Ct);
        }

        var first = await IdOfAsync(await client.PostAsJsonAsync("/api/contents", new
        {
            contentType = type,
            data = new Dictionary<string, object> { ["Badge"] = "X-1" },
        }, Ct));

        (await client.PutAsJsonAsync($"/api/contents/{first}", new
        {
            id = first,
            data = new Dictionary<string, object> { ["Badge"] = "X-2" },
        }, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        await IdOfAsync(await client.PostAsJsonAsync("/api/contents", new
        {
            contentType = type,
            data = new Dictionary<string, object> { ["Badge"] = "X-1" },
        }, Ct));

        Guid createdVersion;
        using (var scope = _fixture.Services.CreateScope())
        {
            var stream = await scope.ServiceProvider.GetRequiredService<IQuerySession>().Events.FetchStreamAsync(first, token: Ct);
            stream.Should().NotBeEmpty();
            createdVersion = stream.First(e => e.Data is ContentCreated).Id;
        }

        var rollback = await client.PostAsJsonAsync($"/api/contents/{first}/rollback/{createdVersion}", new { }, Ct);

        rollback.StatusCode.Should().Be(HttpStatusCode.Conflict, await rollback.Content.ReadAsStringAsync(Ct));

        using var check = _fixture.Services.CreateScope();
        var stored = await check.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<Content>(first, Ct);
        stored!.Data["Badge"].ToString().Should().Be("X-2");
    }

    private static async Task<Guid> IdOfAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    // ---- a workflow writing an entry with no stream -----------------------------------------------

    [Fact]
    public async Task UpdateField_on_an_entry_with_no_stream_cannot_take_a_value_another_entry_holds()
    {
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var tenant = $"unf-{Guid.NewGuid():N}"[..14];
        var type = NewType();
        await StoreTypeAsync(store, tenant, Badges(type));

        var holder = Guid.NewGuid();
        var target = Guid.NewGuid();
        await using (var seed = store.LightweightSession(tenant))
        {
            seed.Store(new Content { Id = holder, ContentType = type, Data = new() { ["Badge"] = "Y-1" } });
            seed.Store(new Content { Id = target, ContentType = type, Data = new() { ["Badge"] = "Y-2" } });
            await seed.SaveChangesAsync(Ct);
        }

        var taken = await RunUpdateFieldAsync(store, tenant, target, "Y-1");
        taken.Succeeded.Should().BeFalse("another entry holds Y-1");

        var free = await RunUpdateFieldAsync(store, tenant, target, "Y-3");
        free.Succeeded.Should().BeTrue(free.Error);

        await using var check = store.QuerySession(tenant);
        (await check.LoadAsync<Content>(target, Ct))!.Data["Badge"].ToString().Should().Be("Y-3");
    }

    private static async Task<barakoCMS.Features.Workflows.WorkflowActionResult> RunUpdateFieldAsync(
        IDocumentStore store, string tenant, Guid contentId, string value)
    {
        await using var session = store.LightweightSession(tenant);
        var writer = new ContentWriter(session, new ContentSourcingPolicyService(session));
        var action = new UpdateFieldAction(
            session, writer, new ContentLifecycleRunner([], session), NullLogger<UpdateFieldAction>.Instance);

        return await action.RunAsync(
            new Dictionary<string, string> { ["Field"] = "data.Badge", ["Value"] = value },
            new Content { Id = contentId, LastModifiedBy = Guid.NewGuid() },
            Ct);
    }

    // ---- the accounting module, which stores accounts around the writer ---------------------------

    /// <summary>
    /// <c>POST /api/accounting/accounts</c> stores the entry with <c>session.Store</c> from the
    /// caller's values, so it applies the account type's rules itself.
    /// </summary>
    [Fact]
    public async Task An_account_created_through_the_accounting_route_is_held_to_the_account_types_rules()
    {
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var tenant = $"una-{Guid.NewGuid():N}"[..14].ToLowerInvariant();
        var userId = Guid.NewGuid();

        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new Tenant { Id = Guid.NewGuid(), Slug = tenant, Name = tenant, IsActive = true });
            session.Store(new User
            {
                Id = userId,
                Username = $"una-{Guid.NewGuid():n}"[..14],
                Email = $"una-{Guid.NewGuid():n}@example.com",
                RoleIds = [SystemRoles.SuperAdminRoleId],
            });
            session.Store(new Membership
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                TenantSlug = tenant,
                Status = MembershipStatus.Active,
                RoleIds = [SystemRoles.SuperAdminRoleId],
            });
            await session.SaveChangesAsync(Ct);
        }

        var account = BarakoCMS.Accounting.AccountingContentTypes.AccountDefinition();
        account.Uniqueness = [new UniquenessRule { Name = "OneAccountPerName", Fields = ["Name"] }];
        await StoreTypeAsync(store, tenant, account);

        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", _fixture.CreateToken(
                roles: ["SuperAdmin", "Admin"],
                userId: userId.ToString(),
                additionalClaims: new Dictionary<string, string> { ["tenant"] = tenant }));
        client.DefaultRequestHeaders.Add("X-Tenant", tenant);

        var first = await client.PostAsJsonAsync("/api/accounting/accounts", new { code = "1000", name = "Cash" }, Ct);
        first.StatusCode.Should().Be(HttpStatusCode.Created, await first.Content.ReadAsStringAsync(Ct));

        var second = await client.PostAsJsonAsync("/api/accounting/accounts", new { code = "1001", name = "Cash" }, Ct);
        var body = await second.Content.ReadAsStringAsync(Ct);
        second.StatusCode.Should().Be(HttpStatusCode.Conflict, body);
        body.Should().Contain("OneAccountPerName");

        await using var check = store.QuerySession(tenant);
        (await check.Query<Content>().CountAsync(c => c.ContentType == account.Name, Ct)).Should().Be(1);
    }
}
