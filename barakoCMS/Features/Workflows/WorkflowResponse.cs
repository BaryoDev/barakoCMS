using barakoCMS.Models;

namespace barakoCMS.Features.Workflows;

/// <summary>A workflow as the API describes it, rather than as it is stored.</summary>
/// <remarks>
/// See <c>Features/Roles/RoleResponse</c> for the reasoning.
///
/// Only the response is separated here. <c>CreateWorkflowEndpoint</c> still binds
/// <see cref="WorkflowDefinition"/> as its request, so the stored shape is still the input contract.
/// That is a larger change than this one, because the request shape is what every caller writes
/// against, and it is noted rather than done.
/// </remarks>
internal sealed class WorkflowResponse
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string TriggerContentType { get; init; } = string.Empty;
    public string TriggerEvent { get; init; } = string.Empty;
    public Dictionary<string, string> Conditions { get; init; } = new();
    public List<WorkflowAction> Actions { get; init; } = new();

    public static WorkflowResponse From(WorkflowDefinition w) => new()
    {
        Id = w.Id,
        Name = w.Name,
        TriggerContentType = w.TriggerContentType,
        TriggerEvent = w.TriggerEvent,
        Conditions = w.Conditions,
        Actions = w.Actions,
    };
}
