using Marten;
using Marten.Events;
using Marten.Events.Projections;
using barakoCMS.Infrastructure.Multitenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using JasperFx.Events;

namespace barakoCMS.Features.Workflows;

// Partial because Marten 9's source generator emits the ApplyAsync dispatcher as an
// override on this class; there is no runtime fallback for conventional Apply methods.
internal partial class WorkflowProjection : EventProjection
{
    private readonly IServiceProvider _serviceProvider;

    public WorkflowProjection(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public async Task Project(IEvent<barakoCMS.Events.ContentUpdated> e, IDocumentOperations ops, CancellationToken ct)
    {
        using var origin = OriginOf(e);
        await ProcessEventAsync(
            barakoCMS.Models.WorkflowEvents.Updated, e.Data.Id, e.TenantId, e.Sequence, ops,
            stored => AsWritten(stored, e.Data.Data, stored.Status));
    }

    public async Task Project(IEvent<barakoCMS.Events.ContentCreated> e, IDocumentOperations ops, CancellationToken ct)
    {
        using var origin = OriginOf(e);
        await ProcessEventAsync(
            barakoCMS.Models.WorkflowEvents.Created, e.Data.Id, e.TenantId, e.Sequence, ops,
            stored => AsWritten(stored, e.Data.Data, e.Data.Status));
    }

    /// <summary>The stored entry as the event left it: its data, and the status given.</summary>
    /// <remarks>
    /// The daemon reads an event after it commits, and by then the document can hold a later save.
    /// Matching conditions against that document matched every earlier event of the entry against
    /// the latest data: a workflow's UpdateField wrote StockEvent Done, a person then saved Waste
    /// before the daemon got there, and both events read Waste, so one save queued the Waste
    /// workflow twice. ContentUpdated and ContentCreated carry the whole data bag (Apply replaces
    /// it), so the event is what that write said. A copy, so the loaded document is never changed.
    /// </remarks>
    internal static barakoCMS.Models.Content AsWritten(
        barakoCMS.Models.Content stored, System.Collections.Generic.Dictionary<string, object>? data, barakoCMS.Models.ContentStatus status) => new()
    {
        Id = stored.Id,
        ContentType = stored.ContentType,
        Data = data ?? stored.Data,
        Status = status,
        Sensitivity = stored.Sensitivity,
        CreatedAt = stored.CreatedAt,
        UpdatedAt = stored.UpdatedAt,
        ScheduledPublishAt = stored.ScheduledPublishAt,
        ScheduledUnpublishAt = stored.ScheduledUnpublishAt,
        ScheduledSensitivity = stored.ScheduledSensitivity,
        ScheduledSensitivityAt = stored.ScheduledSensitivityAt,
        LastModifiedBy = stored.LastModifiedBy,
        LifecycleState = stored.LifecycleState,
        SourceRunId = stored.SourceRunId,
        CreatedBy = stored.CreatedBy,
        BranchId = stored.BranchId,
        SearchText = stored.SearchText,
    };

    public async Task Project(IEvent<barakoCMS.Events.ContentStatusChanged> e, IDocumentOperations ops, CancellationToken ct)
    {
        using var origin = OriginOf(e);

        // Map a status transition to the "Published" trigger event when applicable, so workflows
        // configured with TriggerEvent = "Published" actually fire.
        if (e.Data.NewStatus == barakoCMS.Models.ContentStatus.Published)
        {
            await ProcessEventAsync(barakoCMS.Models.WorkflowEvents.Published, e.Data.Id, e.TenantId, e.Sequence, ops);
            return;
        }

        await ProcessUnpublishedAsync(e, ops, ct);
    }

    /// <remarks>
    /// The event carries only the new status, and the document already holds it by the time the
    /// daemon gets here, so the status before comes from folding the stream up to the event before
    /// this one. That read is only made when a workflow listens for Unpublished on the type. A draft
    /// moved to Archived was never Published and is not an unpublish.
    ///
    /// Nothing but a shutdown escapes, and a failure is kept as a missed trigger as in
    /// ProcessEventAsync. A cancelled read is let through so the daemon takes the event again when
    /// it restarts.
    /// </remarks>
    private async Task ProcessUnpublishedAsync(
        IEvent<barakoCMS.Events.ContentStatusChanged> e, IDocumentOperations ops, CancellationToken ct)
    {
        try
        {
            using var scope = _serviceProvider.CreateScopeForTenant(e.TenantId);
            var queue = scope.ServiceProvider.GetRequiredService<IWorkflowRunQueue>();

            var content = await ops.LoadAsync<barakoCMS.Models.Content>(e.Data.Id, ct);
            if (content is null
                || !await queue.ListensAsync(content.ContentType, barakoCMS.Models.WorkflowEvents.Unpublished, ct))
            {
                return;
            }

            var wasPublished = await WasPublishedBeforeAsync(e, ops, ct);
            if (wasPublished is null)
            {
                // Not guessed. Answering yes would fire Unpublished for a draft moved to Archived.
                var logger = _serviceProvider.GetService<ILogger<WorkflowProjection>>();
                logger?.LogInformation(
                    "The status of content {ContentId} of type {ContentType} before event {Sequence} is unknown, because its stream does not record one, so no Unpublished workflow was fired",
                    content.Id, barakoCMS.Infrastructure.Logging.LogSafe.Value(content.ContentType), e.Sequence);
                return;
            }

            if (wasPublished.Value)
            {
                await queue.EnqueueAsync(content, barakoCMS.Models.WorkflowEvents.Unpublished, e.Sequence, CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var logger = _serviceProvider.GetService<ILogger<WorkflowProjection>>();
            logger?.LogError(ex, "WorkflowProjection failed to process {EventType} for content {ContentId} in tenant {TenantId}", barakoCMS.Models.WorkflowEvents.Unpublished, e.Data.Id, e.TenantId);
            RecordMissed(ops, barakoCMS.Models.WorkflowEvents.Unpublished, e.Data.Id, e.TenantId, e.Sequence, ex);
        }
    }

    /// <summary>
    /// Whether the entry was Published before this event, or null when the stream does not say.
    /// </summary>
    /// <remarks>
    /// An entry stored straight through a session, or seeded, has no ContentCreated in its stream,
    /// so its first status change has nothing before it that records a status.
    /// </remarks>
    internal static async Task<bool?> WasPublishedBeforeAsync(
        IEvent<barakoCMS.Events.ContentStatusChanged> e, IQuerySession query, CancellationToken ct)
    {
        if (e.Version <= 1)
        {
            return null;
        }

        var before = await query.Events.FetchStreamAsync(e.Data.Id, version: e.Version - 1, token: ct);
        if (!before.Any(x => x.Data is barakoCMS.Events.ContentCreated or barakoCMS.Events.ContentStatusChanged))
        {
            return null;
        }

        var prior = barakoCMS.Infrastructure.Services.ContentProjection.Fold(before);
        return prior is { Status: barakoCMS.Models.ContentStatus.Published };
    }

    /// <remarks>
    /// A transition is not folded into Updated. Routing on Updated fires on every save, so an
    /// invoice would be sent to the supplier on every edit before approval and again after, which is
    /// the feature not existing rather than a rough edge.
    ///
    /// Keyed on the transition name and not on the state it lands in. "Status is now Approved" also
    /// describes an administrator correcting a mistake, and a supplier notification is the thing
    /// that most needs to not fire on that.
    /// </remarks>
    public async Task Project(IEvent<barakoCMS.Events.ContentTransitioned> e, IDocumentOperations ops, CancellationToken ct)
    {
        using var origin = OriginOf(e);
        await ProcessEventAsync(
            barakoCMS.Models.WorkflowEvents.ForTransition(e.Data.Transition), e.Data.Id, e.TenantId, e.Sequence, ops);
    }

    /// <summary>
    /// Makes the request that wrote <paramref name="e"/> the origin of whatever is queued for it.
    /// </summary>
    /// <remarks>
    /// The daemon has no request. What it has is the event, which recorded the correlation id of
    /// the request that wrote it and the traceparent of that request's span, and a run queued here
    /// copies both. The stored traceparent is passed as the cause so the run is parented to the
    /// request and never to a span of the daemon. An event stored before the two were kept, or
    /// written by work no request caused, has neither, and the run is queued with neither.
    /// </remarks>
    internal static IDisposable OriginOf(IEvent e) =>
        barakoCMS.Infrastructure.Tracing.Correlation.BeginFrom(e.CorrelationId, e.CausationId);

    private async Task ProcessEventAsync(
        string eventType, Guid contentId, string tenantId, long sequence, IDocumentOperations ops,
        Func<barakoCMS.Models.Content, barakoCMS.Models.Content>? asOfEvent = null)
    {
        // This runs inside Marten's async projection daemon. The host sets no error handling for
        // projections, so the JasperFx defaults apply: a transient failure is retried by the daemon,
        // and a failure that is not transient is dead-lettered. Nothing replays a dead letter. A
        // failure is caught here instead and kept as a MissedWorkflowTrigger, staged on the daemon's
        // own batch, and MissedTriggerSweep replays it. Catching a transient failure gives up the
        // daemon's retry, and the sweep takes its place. A rebuild re-runs every action for every
        // event ever stored, so it is not a way to get one run back.
        try
        {
            // The scope has to carry the event's tenant. A plain CreateScope() lands on the default
            // partition, where a tenant's workflow definitions do not exist and a workflow action's
            // writes would cross the isolation boundary.
            using var scope = _serviceProvider.CreateScopeForTenant(tenantId);
            var queue = scope.ServiceProvider.GetRequiredService<IWorkflowRunQueue>();

            var content = await ops.LoadAsync<barakoCMS.Models.Content>(contentId);
            if (content != null)
            {
                // Queued, not executed. This runs inside the daemon, which processes a shard
                // sequentially: an action posting to three third parties used to hold the shard for
                // the duration of all three, so one slow provider stalled workflow processing for
                // every tenant and a hanging one stopped it. Writing a run is one Postgres round
                // trip, and WorkflowRunner does the I/O outside this path.
                await queue.EnqueueAsync(asOfEvent is null ? content : asOfEvent(content), eventType, sequence, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            var logger = _serviceProvider.GetService<ILogger<WorkflowProjection>>();
            logger?.LogError(ex, "WorkflowProjection failed to process {EventType} for content {ContentId} in tenant {TenantId}", eventType, contentId, tenantId);
            RecordMissed(ops, eventType, contentId, tenantId, sequence, ex);
        }
    }

    /// <summary>
    /// Stages a row saying this event's runs were not queued, for <see cref="MissedTriggerSweep"/>
    /// to replay.
    /// </summary>
    /// <remarks>
    /// Staged on the daemon's operations and not written in a session of its own, so the row commits
    /// with the shard's progress for the event. When Postgres is down the batch fails, and the
    /// daemon retries the event instead of moving past it. Named for the event's tenant, so the row does not
    /// depend on which tenant the operations were opened for. Nothing here reads or
    /// writes the database, so it cannot fail for that reason, and a failure to stage is logged and
    /// swallowed so the original failure is not replaced by this one.
    /// </remarks>
    private void RecordMissed(
        IDocumentOperations ops, string eventType, Guid contentId, string tenantId, long sequence, Exception cause)
    {
        try
        {
            ops.ForTenant(tenantId).Store(MissedWorkflowTrigger.For(eventType, contentId, tenantId, sequence, cause));
        }
        catch (Exception ex)
        {
            var logger = _serviceProvider.GetService<ILogger<WorkflowProjection>>();
            logger?.LogError("WorkflowProjection could not stage the missed {EventType} trigger for content {ContentId} in tenant {TenantId} ({ErrorType}), so no run will be made for it",
                barakoCMS.Infrastructure.Logging.LogSafe.Value(eventType), contentId, barakoCMS.Infrastructure.Logging.LogSafe.Value(tenantId), ex.GetType().Name);
        }
    }
}
