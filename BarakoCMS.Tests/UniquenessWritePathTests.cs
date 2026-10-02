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
    /// The first write holds the lock until it commits. Without the lock the second write reads no
    /// holder, because the first is not committed, and both land.
    /// </summary>
    [Fact]
    public async Task A_second_write_of_a_held_value_waits_for_the_first_to_commit_and_is_then_refused()
    {
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var tenant = $"unl-{Guid.NewGuid():N}"[..14];
        var type = NewType();
        await StoreTypeAsync(store, tenant, Badges(type));

        await using var first = store.LightweightSession(tenant);
        await new ContentWriter(first, new ContentSourcingPolicyService(first)).CreateAsync(Created(type, "B-7"), Ct);

        await using var second = store.LightweightSession(tenant);
        var secondWriter = new ContentWriter(second, new ContentSourcingPolicyService(second));
        var racing = Task.Run(async () =>
        {
            await secondWriter.CreateAsync(Created(type, "B-7"), Ct);
            await second.SaveChangesAsync(Ct);
        }, Ct);

        var finished = await Task.WhenAny(racing, Task.Delay(TimeSpan.FromSeconds(2), Ct));
        finished.Should().NotBeSameAs(racing, "the second write waits on the lock the first one holds");

        await first.SaveChangesAsync(Ct);

        var outcome = async () => await racing.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await outcome.Should().ThrowAsync<ContentUniquenessException>();

        (await CountAsync(store, tenant, type)).Should().Be(1);
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
}
