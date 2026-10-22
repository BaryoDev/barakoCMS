using barakoCMS.Models;

namespace barakoCMS.Features.Workflows;

/// <summary>Reports the module-prefixed type for an action the module wrote.</summary>
internal sealed class ModuleWorkflowAction(IWorkflowAction inner, string type) : IWorkflowAction
{
    public IWorkflowAction Inner { get; } = inner;
    public string Type { get; } = type;

#pragma warning disable CS0618 // forwarding the deprecated member keeps the wrapped action whole
    public Task ExecuteAsync(Dictionary<string, string> parameters, Models.Content content, CancellationToken ct) =>
        Inner.ExecuteAsync(parameters, content, ct);
#pragma warning restore CS0618

    public Task<WorkflowActionResult> RunAsync(Dictionary<string, string> parameters, Models.Content content, CancellationToken ct) =>
        Inner.RunAsync(parameters, content, ct);

    public Task<WorkflowActionResult> RunAsync(WorkflowContext context, CancellationToken ct) =>
        Inner.RunAsync(context, ct);
}
