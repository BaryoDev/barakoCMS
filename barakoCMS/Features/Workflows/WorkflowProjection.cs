using Marten;
using Marten.Events;
using Marten.Events.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using JasperFx.Events;

namespace barakoCMS.Features.Workflows;

public class WorkflowProjection : EventProjection
{
    private readonly IServiceProvider _serviceProvider;

    public WorkflowProjection(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public async Task Project(IEvent<barakoCMS.Events.ContentUpdated> e, IDocumentOperations ops, CancellationToken ct)
    {
        await ProcessEventAsync(barakoCMS.Models.WorkflowEvents.Updated, e.Data.Id, ops);
    }

    public async Task Project(IEvent<barakoCMS.Events.ContentCreated> e, IDocumentOperations ops, CancellationToken ct)
    {
        await ProcessEventAsync(barakoCMS.Models.WorkflowEvents.Created, e.Data.Id, ops);
    }

    public async Task Project(IEvent<barakoCMS.Events.ContentStatusChanged> e, IDocumentOperations ops, CancellationToken ct)
    {
        // Map a status transition to the "Published" trigger event when applicable, so workflows
        // configured with TriggerEvent = "Published" actually fire (previously nothing emitted it).
        if (e.Data.NewStatus == barakoCMS.Models.ContentStatus.Published)
        {
            await ProcessEventAsync(barakoCMS.Models.WorkflowEvents.Published, e.Data.Id, ops);
        }
    }

    private async Task ProcessEventAsync(string eventType, Guid contentId, IDocumentOperations ops)
    {
        // This runs inside Marten's async projection daemon. Any unhandled exception here stops the
        // projection shard and halts ALL workflows until a manual rebuild — so nothing may escape.
        try
        {
            using var scope = _serviceProvider.CreateScope();
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
            logger?.LogError(ex, "WorkflowProjection failed to process {EventType} for content {ContentId}", eventType, contentId);
        }
    }
}
