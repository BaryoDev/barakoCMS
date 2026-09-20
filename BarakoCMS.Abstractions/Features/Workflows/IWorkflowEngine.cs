using barakoCMS.Models;

namespace barakoCMS.Features.Workflows;

public interface IWorkflowEngine
{
    Task ProcessEventAsync(string contentType, string eventType, barakoCMS.Models.Content content, CancellationToken ct);
}
