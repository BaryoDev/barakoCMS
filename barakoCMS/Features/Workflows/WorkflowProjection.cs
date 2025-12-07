using Marten;
using Marten.Events;
using Marten.Events.Projections;
using Microsoft.Extensions.DependencyInjection;
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
        await ProcessEventAsync("Updated", e.Data.Id, e.StreamId, ops);
    }

    public async Task Project(IEvent<barakoCMS.Events.ContentCreated> e, IDocumentOperations ops, CancellationToken ct)
    {
        await ProcessEventAsync("Created", e.Data.Id, e.StreamId, ops);
    }

    private async Task ProcessEventAsync(string eventType, Guid contentId, Guid streamId, IDocumentOperations ops)
    {
        // Create a scope to resolve scoped services like WorkflowEngine
        using var scope = _serviceProvider.CreateScope();
        var workflowEngine = scope.ServiceProvider.GetRequiredService<IWorkflowEngine>();

        // We need the full Content object. 
        // Since we are in a Projection, we might need to load it. 
        // 'ops' allows loading.
        var content = await ops.LoadAsync<barakoCMS.Models.Content>(contentId);

        if (content != null)
        {
            // We pass CancellationToken.None or a timeout token
            await workflowEngine.ProcessEventAsync(content.ContentType, eventType, content, CancellationToken.None);
        }
    }
}
