using System.Text.Json;
using barakoCMS.Core;
using barakoCMS.Core.Interfaces;
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
/// has no downstream, so several tests below drive that exact sequence rather than the happy path:
/// two calls carrying the parameters the runner would inject for the same attempt must change the
/// content once, two calls for genuinely different attempts must both take effect, and a marker
/// stored through its own document, rather than the content's own Data, must survive an edit to
/// that content landing in between the two.
///
/// Each call opens its own session, the way each node's own pass through
/// <c>WorkflowRunner.TryRunAsync</c> does, rather than reusing one session for both. Reusing a
/// session would let Marten's identity map hand back the first call's own in-memory document on the
/// second call's "reload", which would make the guard look like it works even if it read the
/// session's cache instead of what the other writer had actually committed. The two atomicity tests
/// are the deliberate exception: they reuse a session precisely to control what it has and has not
/// seen, which is explained where each one does it.
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
    /// apply removes it rather than letting a workflow that touches an item often, "decrement stock
    /// on every order" is the ticket's own example, grow one marker row per run forever.
    /// </summary>
    [Fact]
    public async Task A_stale_marker_is_pruned_on_the_next_apply()
    {
        const string tenant = "update-field-prune";
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var contentId = Guid.NewGuid();

        await SeedAsync(store, tenant, contentId, "update-field-stock", ContentStatus.Published,
            new Dictionary<string, object> { { "Stock", "10" } });

        var staleKey = $"{Guid.NewGuid()}:0";
        await using (var seedMarker = store.LightweightSession(tenant))
        {
            seedMarker.Store(new WorkflowFieldApplyMarker
            {
                Key = staleKey,
                Attempt = 1,
                AppliedAt = DateTimeOffset.UtcNow - UpdateFieldAction.MarkerRetention - TimeSpan.FromMinutes(1),
            });
            await seedMarker.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var runId = Guid.NewGuid();
        var freshKey = $"{runId}:0";
        await RunOnceAsync(store, tenant, contentId, new Dictionary<string, string>
        {
            { "Field", "data.Stock" },
            { "Value", "9" },
            { "IdempotencyKey", freshKey },
            { "RunId", runId.ToString() },
            { "Attempt", "1" },
        });

        await using var check = store.QuerySession(tenant);

        var staleMarker = await check.LoadAsync<WorkflowFieldApplyMarker>(staleKey, TestContext.Current.CancellationToken);
        staleMarker.Should().BeNull("a marker past MarkerRetention must be pruned on the next apply");

        var freshMarker = await check.LoadAsync<WorkflowFieldApplyMarker>(freshKey, TestContext.Current.CancellationToken);
        freshMarker.Should().NotBeNull("the new attempt's own marker must still be recorded");
    }

    /// <summary>
    /// The proof the marker had to move off Content.Data (see the remarks on
    /// <see cref="WorkflowFieldApplyMarker"/>): a human edit lands between the apply and the
    /// reclaimed rerun, and the change must still have applied exactly once afterwards. Under the
    /// old design the edit's wholesale replacement of Data would have wiped the marker along with
    /// it, and the reclaimed rerun would have read that as a brand new attempt.
    /// </summary>
    [Fact]
    public async Task A_human_edit_between_the_apply_and_the_reclaimed_rerun_still_leaves_the_change_applied_once()
    {
        const string tenant = "update-field-survives-edit";
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var contentId = Guid.NewGuid();

        await SeedAsync(store, tenant, contentId, "update-field-stock", ContentStatus.Published,
            new Dictionary<string, object> { { "Stock", "10" } });

        var runId = Guid.NewGuid();
        var parameters = new Dictionary<string, string>
        {
            { "Field", "data.Stock" },
            { "Value", "9" },
            { "IdempotencyKey", $"{runId}:0" },
            { "RunId", runId.ToString() },
            { "Attempt", "1" },
        };

        // The node whose outcome write the runner later discards, per the ticket.
        await RunOnceAsync(store, tenant, contentId, parameters);
        var afterFirstApply = await FetchVersionAsync(store, tenant, contentId);

        // A human edit lands in between: PUT /api/contents/{id} replaces Data wholesale with
        // whatever the client sent (Content/Update/Endpoint.cs; Content.Apply(ContentUpdated, ...)
        // sets Data = the event's Data, not a merge). A schema-driven edit form never re-sends a
        // field it does not know about, so this carries no Stock, the way a save of an unrelated
        // field genuinely would.
        await using (var editSession = store.LightweightSession(tenant))
        {
            var editWriter = new ContentWriter(editSession, new ContentSourcingPolicyService(editSession));
            var beforeEdit = await editSession.LoadAsync<Content>(contentId, TestContext.Current.CancellationToken);
            var editEvent = new ContentUpdated(
                contentId, new Dictionary<string, object> { { "Priority", "High" } }, Guid.NewGuid(), null, DateTime.UtcNow);

            await editWriter.AppendOptimisticAsync(beforeEdit!, new object[] { editEvent }, TestContext.Current.CancellationToken);
            await editSession.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var afterHumanEdit = await FetchVersionAsync(store, tenant, contentId);
        afterHumanEdit.Should().Be(afterFirstApply + 1);

        var editedContent = await LoadAsync(store, tenant, contentId);
        editedContent.Data.Should().NotContainKey(
            "Stock", "the edit's Data replaced the document's Data wholesale, the way Content.Apply(ContentUpdated) always has");

        // The node that reclaimed the lease and reruns the SAME attempt. If the marker had lived on
        // Data, the design this test rules out, the edit above would have erased it and this call
        // would have read as a brand new attempt rather than the same one replaying.
        await RunOnceAsync(store, tenant, contentId, parameters);

        var afterReclaim = await FetchVersionAsync(store, tenant, contentId);
        afterReclaim.Should().Be(afterHumanEdit, "the same attempt replaying after an unrelated edit must still be a no-op");
    }

    /// <summary>
    /// One half of the atomicity the marker's own document is supposed to give: staged in the same
    /// session as the content write, so a refused content write must leave no marker behind either.
    /// </summary>
    /// <remarks>
    /// Forcing <c>UpdateFieldAction</c>'s own <c>AppendOptimisticAsync</c> call to fail
    /// deterministically needs a genuine race between two writers, which nothing in a single test
    /// process can reliably arrange. <c>IContentWriter.AppendAsync</c>'s explicit-version overload,
    /// the one <c>Content/Update/Endpoint.cs</c> uses, is refused synchronously and for the same
    /// underlying reason a real reclaim would be, the version the caller thought it held is not the
    /// version the stream is actually at, so it proves the same guarantee without needing a race:
    /// the marker is staged first, exactly where <c>UpdateFieldAction</c> stages its own, and the
    /// content write after it never gets as far as <c>SaveChangesAsync</c>.
    /// </remarks>
    [Fact]
    public async Task A_failed_content_write_leaves_no_marker_behind()
    {
        const string tenant = "update-field-atomic-content-fails";
        var contentType = "update-field-atomic-content-fails-type";
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var contentId = Guid.NewGuid();

        // Event sourced, so a wrong expected version is refused rather than silently overwritten;
        // document mode keeps last-write-wins and has no such refusal to force.
        await using (var seedPolicy = store.LightweightSession(tenant))
        {
            seedPolicy.Store(new ContentTypeSourcingPolicy { Name = ContentTypeName.Normalize(contentType), EventSourced = true });
            await seedPolicy.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await SeedAsync(store, tenant, contentId, contentType, ContentStatus.Published,
            new Dictionary<string, object> { { "Stock", "10" } });

        var idempotencyKey = $"{Guid.NewGuid()}:0";

        await using var session = store.LightweightSession(tenant);
        var writer = new ContentWriter(session, new ContentSourcingPolicyService(session));
        var targetContent = await session.LoadAsync<Content>(contentId, TestContext.Current.CancellationToken);

        // Staged exactly where UpdateFieldAction stages it: before the content write is attempted,
        // in the same session, so the same SaveChangesAsync would commit or refuse both together.
        session.Store(new WorkflowFieldApplyMarker { Key = idempotencyKey, Attempt = 1, AppliedAt = DateTimeOffset.UtcNow });

        var updateEvent = new ContentUpdated(
            contentId, new Dictionary<string, object> { { "Stock", "9" } }, Guid.NewGuid(), null, DateTime.UtcNow);

        var act = () => writer.AppendAsync(targetContent!, new object[] { updateEvent }, expectedVersion: 999, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<StaleContentException>(
            "the caller's version does not match the stream, the same disagreement a genuine reclaim race would produce");

        // SaveChangesAsync is never reached. The staged marker is discarded with the session,
        // unsaved: this is the whole of what makes the proof work, not an extra assertion on top.

        var reloadedContent = await LoadAsync(store, tenant, contentId);
        AsString(reloadedContent.Data["Stock"]).Should().Be("10", "the refused content write must not have applied");

        await using var checkSession = store.QuerySession(tenant);
        var marker = await checkSession.LoadAsync<WorkflowFieldApplyMarker>(idempotencyKey, TestContext.Current.CancellationToken);
        marker.Should().BeNull("the marker was only ever staged alongside the refused content write, and was never saved");
    }

    /// <summary>
    /// The other half: staged in the same session as the content write, so a refused marker write
    /// must leave the content unchanged, rather than the field applying with nothing recording it.
    /// </summary>
    /// <remarks>
    /// The marker document has <c>UseOptimisticConcurrency(true)</c> (see its registration in
    /// ServiceCollectionExtensions), the same as <c>WorkflowRun</c> and for the same reason: loading
    /// it, deciding, and saving it is a read, a check and a write with nothing between them. This
    /// forces that check to fail deterministically, without a race, by giving <c>sessionA</c> a
    /// stale view on purpose: it loads the marker before <c>sessionB</c> updates and commits a
    /// change to it, so when sessionA later tries to save its own update, Marten compares what
    /// sessionA's identity map remembers against what is actually in the database and finds they
    /// have diverged.
    /// </remarks>
    [Fact]
    public async Task A_failed_marker_write_leaves_content_unchanged()
    {
        const string tenant = "update-field-atomic-marker-fails";
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var contentId = Guid.NewGuid();
        var idempotencyKey = $"{Guid.NewGuid()}:0";

        await SeedAsync(store, tenant, contentId, "update-field-stock", ContentStatus.Published,
            new Dictionary<string, object> { { "Stock", "10" } });

        await using (var seedMarker = store.LightweightSession(tenant))
        {
            seedMarker.Store(new WorkflowFieldApplyMarker { Key = idempotencyKey, Attempt = 1, AppliedAt = DateTimeOffset.UtcNow });
            await seedMarker.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // An identity session rather than the lightweight one UpdateFieldAction is actually given
        // in production: a lightweight session tracks no identity map, so its second load of the
        // same id is a fresh query rather than the cached, stale copy this test needs. The identity
        // map is standing in here for real elapsed time; a genuinely concurrent node's own
        // lightweight session would arrive at the same staleness by querying at an earlier moment,
        // not by caching, but the version comparison Marten makes at save time is identical either
        // way.
        await using var sessionA = store.IdentitySession(tenant);

        // Pins sessionA's identity map to the marker as it stood just above (Attempt 1), the way
        // UpdateFieldAction's own load would if it ran right now.
        _ = await sessionA.LoadAsync<WorkflowFieldApplyMarker>(idempotencyKey, TestContext.Current.CancellationToken);

        // A concurrent writer that gets there first: loads the same marker, moves it to Attempt 5,
        // and commits before sessionA does anything further.
        await using (var sessionB = store.LightweightSession(tenant))
        {
            var seenByB = await sessionB.LoadAsync<WorkflowFieldApplyMarker>(idempotencyKey, TestContext.Current.CancellationToken);
            seenByB!.Attempt = 5;
            seenByB.AppliedAt = DateTimeOffset.UtcNow;
            sessionB.Store(seenByB);
            await sessionB.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // sessionA now runs UpdateFieldAction for Attempt 3, distinct from both 1 (its own stale
        // view) and 5 (what sessionB actually committed), so it decides this looks like a genuine
        // new attempt and proceeds to apply, using the session whose identity map is out of date.
        await RunOnceAsync(sessionA, contentId, new Dictionary<string, string>
        {
            { "Field", "data.Stock" },
            { "Value", "9" },
            { "IdempotencyKey", idempotencyKey },
            { "Attempt", "3" },
        });

        var content = await LoadAsync(store, tenant, contentId);
        AsString(content.Data["Stock"]).Should().Be("10", "sessionA's refused save must not have changed the content");

        await using var checkSession = store.QuerySession(tenant);
        var marker = await checkSession.LoadAsync<WorkflowFieldApplyMarker>(idempotencyKey, TestContext.Current.CancellationToken);
        marker!.Attempt.Should().Be(5, "sessionB's own commit must stand; sessionA's conflicting one must not have overwritten it");
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
        await RunOnceAsync(session, contentId, parameters);
    }

    /// <summary>
    /// Runs the action against a caller-supplied session rather than a fresh one, for the tests that
    /// need to control what the session has already seen.
    /// </summary>
    private static async Task RunOnceAsync(IDocumentSession session, Guid contentId, Dictionary<string, string> parameters)
    {
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
