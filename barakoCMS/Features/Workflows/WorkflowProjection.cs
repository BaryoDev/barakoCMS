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
        await ProcessEventAsync(barakoCMS.Models.WorkflowEvents.Updated, e.Data.Id, e.TenantId, ops);
    }

    public async Task Project(IEvent<barakoCMS.Events.ContentCreated> e, IDocumentOperations ops, CancellationToken ct)
    {
        await ProcessEventAsync(barakoCMS.Models.WorkflowEvents.Created, e.Data.Id, e.TenantId, ops);
    }

    public async Task Project(IEvent<barakoCMS.Events.ContentStatusChanged> e, IDocumentOperations ops, CancellationToken ct)
    {
        // Map a status transition to the "Published" trigger event when applicable, so workflows
        // configured with TriggerEvent = "Published" actually fire.
        if (e.Data.NewStatus == barakoCMS.Models.ContentStatus.Published)
        {
            await ProcessEventAsync(barakoCMS.Models.WorkflowEvents.Published, e.Data.Id, e.TenantId, ops);
        }
    }

    private async Task ProcessEventAsync(string eventType, Guid contentId, string tenantId, IDocumentOperations ops)
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
            var workflowEngine = scope.ServiceProvider.GetRequiredService<IWorkflowEngine>();

            var content = await ops.LoadAsync<barakoCMS.Models.Content>(contentId);
            if (content != null)
            {
                await workflowEngine.ProcessEventAsync(content.ContentType, eventType, content, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            var logger = _serviceProvider.GetService<ILogger<WorkflowProjection>>();
            logger?.LogError(ex, "WorkflowProjection failed to process {EventType} for content {ContentId} in tenant {TenantId}", eventType, contentId, tenantId);
        }
    }
}
