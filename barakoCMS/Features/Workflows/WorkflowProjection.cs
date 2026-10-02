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
        await ProcessEventAsync(barakoCMS.Models.WorkflowEvents.Updated, e.Data.Id, e.TenantId, e.Sequence, ops);
    }

    public async Task Project(IEvent<barakoCMS.Events.ContentCreated> e, IDocumentOperations ops, CancellationToken ct)
    {
        await ProcessEventAsync(barakoCMS.Models.WorkflowEvents.Created, e.Data.Id, e.TenantId, e.Sequence, ops);
    }

    public async Task Project(IEvent<barakoCMS.Events.ContentStatusChanged> e, IDocumentOperations ops, CancellationToken ct)
    {
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
    /// Nothing but a shutdown may escape, for the reason given in ProcessEventAsync. A cancelled
    /// read is let through so the daemon takes the event again when it restarts.
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
        }
    }

    /// <summary>
    /// Whether the entry was Published before this event, or null when the stream does not say.
    /// </summary>
    /// <remarks>
    /// An entry stored straight through a session, or seeded, has no ContentCreated in its stream,
    /// so its first status change has nothing before it that records a status.
    /// </remarks>
    private static async Task<bool?> WasPublishedBeforeAsync(
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
        await ProcessEventAsync(
            barakoCMS.Models.WorkflowEvents.ForTransition(e.Data.Transition), e.Data.Id, e.TenantId, e.Sequence, ops);
    }

    private async Task ProcessEventAsync(
        string eventType, Guid contentId, string tenantId, long sequence, IDocumentOperations ops)
    {
        // This runs inside Marten's async projection daemon. Any unhandled exception here stops the
        // projection shard, and every workflow stops firing with no further signal in the logs, so
        // nothing may escape. There is no cheap remedy for that state: restarting resumes at the
        // same event, and a rebuild re-runs every action for every event ever stored, so it re-sends
        // every email and re-fires every webhook. The Workflow Projection health check is what makes
        // the state visible. See docs/operating-workflows.md.
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
                await queue.EnqueueAsync(content, eventType, sequence, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            var logger = _serviceProvider.GetService<ILogger<WorkflowProjection>>();
            logger?.LogError(ex, "WorkflowProjection failed to process {EventType} for content {ContentId} in tenant {TenantId}", eventType, contentId, tenantId);
        }
    }
}
