using System.Text.Json;
using barakoCMS.Events;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// <c>UpdateFieldAction</c> against a real store, because the fix it tests (issue #571) is about
/// what actually lands in the database across two independent writers, not about which methods get
/// called on a mock.
/// </summary>
/// <remarks>
/// The scenario the ticket names: <c>WorkflowRunner.TryRunAsync</c> discards the outcome of an
/// attempt whose lease was reclaimed while it ran, on purpose (see the comment above that check),
/// trusting the idempotency key to absorb the duplicate call downstream. An in-process field update
/// has no downstream, so the two tests below drive that exact sequence rather than the happy path:
/// two calls carrying the parameters the runner would inject for the same attempt must change the
/// content once, and two calls for genuinely different attempts must both take effect.
///
/// Each call opens its own session, the way each node's own pass through
/// <c>WorkflowRunner.TryRunAsync</c> does, rather than reusing one session for both. Reusing a
/// session would let Marten's identity map hand back the first call's own in-memory document on the
/// second call's "reload", which would make the guard look like it works even if it read the
/// session's cache instead of what the other writer had actually committed.
/// </remarks>
[Collection("Sequential")]
public class UpdateFieldActionTests
{
    private readonly IntegrationTestFixture _fixture;

    public UpdateFieldActionTests(IntegrationTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_data_field_update_is_applied()
    {
        const string tenant = "update-field-data";
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var contentId = Guid.NewGuid();

        await SeedAsync(store, tenant, contentId, "update-field-task", ContentStatus.Draft,
            new Dictionary<string, object> { { "Priority", "Low" } });

        var parameters = new Dictionary<string, string>
        {
            { "Field", "data.Priority" },
            { "Value", "High" },
        };

        await RunOnceAsync(store, tenant, contentId, parameters);

        var updated = await LoadAsync(store, tenant, contentId);
        AsString(updated.Data["Priority"]).Should().Be("High");
    }

    [Fact]
    public async Task A_status_update_is_applied()
    {
        const string tenant = "update-field-status";
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var contentId = Guid.NewGuid();

        await SeedAsync(store, tenant, contentId, "update-field-task", ContentStatus.Draft, new Dictionary<string, object>());

        var parameters = new Dictionary<string, string>
        {
            { "Field", "Status" },
            { "Value", "Published" },
        };

        await RunOnceAsync(store, tenant, contentId, parameters);

        var updated = await LoadAsync(store, tenant, contentId);
        updated.Status.Should().Be(ContentStatus.Published);
    }

    /// <summary>
    /// The scenario the ticket is named for: the same attempt runs to completion twice because its
    /// first outcome write was discarded, and the second run must not decrement the stock a second
    /// time.
    /// </summary>
    [Fact]
    public async Task A_reclaimed_attempt_does_not_apply_its_change_twice()
    {
        const string tenant = "update-field-reclaim";
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var contentId = Guid.NewGuid();

        await SeedAsync(store, tenant, contentId, "update-field-stock", ContentStatus.Published,
            new Dictionary<string, object> { { "Stock", "10" } });

        // What WorkflowRunner.ExecuteAsync injects for one attempt of one action (see the comment
        // there above resolved["IdempotencyKey"]): stable across every rerun of this attempt.
        var runId = Guid.NewGuid();
        var parameters = new Dictionary<string, string>
        {
            { "Field", "data.Stock" },
            { "Value", "9" },
            { "IdempotencyKey", $"{runId}:0" },
            { "RunId", runId.ToString() },
            { "Attempt", "1" },
        };

        // The node that claimed the lease, runs to completion, and (per the ticket) never gets to
        // write its outcome back because another node already reclaimed the attempt.
        await RunOnceAsync(store, tenant, contentId, parameters);

        var afterFirstRun = await FetchVersionAsync(store, tenant, contentId);

        // The node that reclaimed the lease and ran the SAME attempt again. Same RunId, same
        // Ordinal (folded into the same IdempotencyKey), same Attempt: this is not a retry, it is
        // the identical attempt running twice.
        await RunOnceAsync(store, tenant, contentId, parameters);

        var afterSecondRun = await FetchVersionAsync(store, tenant, contentId);

        afterSecondRun.Should().Be(afterFirstRun, "the second run is the same attempt replaying, not a new one, and must write nothing");

        var updated = await LoadAsync(store, tenant, contentId);
        AsString(updated.Data["Stock"]).Should().Be("9", "the stock must be decremented once, not twice");
    }

    /// <summary>
    /// The other direction the ticket calls out by name: a retry after a real failure carries a new
    /// Attempt value (WorkflowRunner only advances it once a terminal outcome is recorded), and that
    /// one must still take effect.
    /// </summary>
    [Fact]
    public async Task A_genuine_retry_after_a_real_failure_still_applies()
    {
        const string tenant = "update-field-retry";
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var contentId = Guid.NewGuid();

        await SeedAsync(store, tenant, contentId, "update-field-stock", ContentStatus.Published,
            new Dictionary<string, object> { { "Stock", "10" } });

        var runId = Guid.NewGuid();
        var idempotencyKey = $"{runId}:0";

        await RunOnceAsync(store, tenant, contentId, new Dictionary<string, string>
        {
            { "Field", "data.Stock" },
            { "Value", "9" },
            { "IdempotencyKey", idempotencyKey },
            { "RunId", runId.ToString() },
            { "Attempt", "1" },
        });

        var afterFirstAttempt = await FetchVersionAsync(store, tenant, contentId);

        // Same IdempotencyKey (same run, same action position), a later Attempt: WorkflowRunner
        // only reaches Attempt 2 once Attempt 1 was recorded as a real, terminal failure, so this
        // is the genuine retry the ticket says must not be swallowed by the guard above.
        await RunOnceAsync(store, tenant, contentId, new Dictionary<string, string>
        {
            { "Field", "data.Stock" },
            { "Value", "8" },
            { "IdempotencyKey", idempotencyKey },
            { "RunId", runId.ToString() },
            { "Attempt", "2" },
        });

        var afterSecondAttempt = await FetchVersionAsync(store, tenant, contentId);

        afterSecondAttempt.Should().Be(afterFirstAttempt + 1, "a different Attempt is a genuine retry and must be applied");

        var updated = await LoadAsync(store, tenant, contentId);
        AsString(updated.Data["Stock"]).Should().Be("8");
    }

    /// <summary>
    /// A marker past <c>MarkerRetention</c> is dead weight (see the remarks on that constant: its
    /// own run can never reclaim and rerun again by the time it is that old), and the next real
    /// apply removes it rather than letting an item that a workflow touches often, "decrement stock
    /// on every order" is the ticket's own example, grow one marker key per run forever.
    /// </summary>
    [Fact]
    public async Task A_stale_marker_is_pruned_on_the_next_apply()
    {
        const string tenant = "update-field-prune";
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var contentId = Guid.NewGuid();

        var staleKey = UpdateFieldAction.AppliedMarkerPrefix + "stale-run:0";
        var staleValue = UpdateFieldAction.FormatMarker(
            "1", DateTimeOffset.UtcNow - UpdateFieldAction.MarkerRetention - TimeSpan.FromMinutes(1));

        await SeedAsync(store, tenant, contentId, "update-field-stock", ContentStatus.Published,
            new Dictionary<string, object> { { "Stock", "10" }, { staleKey, staleValue } });

        var runId = Guid.NewGuid();
        await RunOnceAsync(store, tenant, contentId, new Dictionary<string, string>
        {
            { "Field", "data.Stock" },
            { "Value", "9" },
            { "IdempotencyKey", $"{runId}:0" },
            { "RunId", runId.ToString() },
            { "Attempt", "1" },
        });

        var updated = await LoadAsync(store, tenant, contentId);

        updated.Data.Should().NotContainKey(staleKey, "a marker past MarkerRetention must be pruned on the next apply");
        updated.Data.Keys.Should().Contain(
            k => k.StartsWith(UpdateFieldAction.AppliedMarkerPrefix, StringComparison.Ordinal),
            "the new attempt's own marker must still be recorded");
    }

    /// <summary>
    /// Creates content the way this system actually creates it, through <c>IContentWriter</c>, so
    /// its event stream exists before the action appends to it. A raw <c>session.Store</c> would
    /// leave the stream unstarted, which every real caller here avoids by going through the writer.
    /// </summary>
    private static async Task SeedAsync(
        IDocumentStore store, string tenant, Guid contentId, string contentType, ContentStatus status,
        Dictionary<string, object> data)
    {
        await using var seed = store.LightweightSession(tenant);
        var writer = new ContentWriter(seed, new ContentSourcingPolicyService(seed));

        await writer.CreateAsync(
            new ContentCreated(contentId, contentType, data, status, Guid.NewGuid(), null, SensitivityLevel.Public, DateTime.UtcNow),
            TestContext.Current.CancellationToken);

        await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Runs the action once, in a session of its own, standing in for one node's own pass.</summary>
    private static async Task RunOnceAsync(
        IDocumentStore store, string tenant, Guid contentId, Dictionary<string, string> parameters)
    {
        await using var session = store.LightweightSession(tenant);
        var writer = new ContentWriter(session, new ContentSourcingPolicyService(session));
        var action = new UpdateFieldAction(session, writer, NullLogger<UpdateFieldAction>.Instance);

        // Stands in for the content WorkflowRunner.ExecuteAsync loads before calling the handler.
        // Only Id and LastModifiedBy are read from it when no TargetId parameter is set; the action
        // reloads the real, current document itself before deciding anything.
        var triggerContent = new Content { Id = contentId, LastModifiedBy = Guid.NewGuid() };

        await action.ExecuteAsync(parameters, triggerContent, TestContext.Current.CancellationToken);
    }

    private static async Task<Content> LoadAsync(IDocumentStore store, string tenant, Guid contentId)
    {
        await using var session = store.QuerySession(tenant);
        var content = await session.LoadAsync<Content>(contentId, TestContext.Current.CancellationToken);
        content.Should().NotBeNull();
        return content!;
    }

    private static async Task<long> FetchVersionAsync(IDocumentStore store, string tenant, Guid contentId)
    {
        await using var session = store.QuerySession(tenant);
        var state = await session.Events.FetchStreamStateAsync(contentId, TestContext.Current.CancellationToken);
        state.Should().NotBeNull("the seed itself starts a stream, so a stream must exist by the time this is called");
        return state!.Version;
    }

    private static string? AsString(object? value) => value switch
    {
        null => null,
        string s => s,
        JsonElement je => je.ToString(),
        _ => value.ToString(),
    };
}
