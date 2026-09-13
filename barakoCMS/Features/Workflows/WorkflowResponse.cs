using barakoCMS.Features.Workflows.Actions;
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

    /// <summary>Every content type the workflow fires for, including <see cref="TriggerContentType"/>.</summary>
    /// <remarks>Filled for a workflow saved before the list existed too, so a reader can use this alone.</remarks>
    public List<string> TriggerContentTypes { get; init; } = new();
    public string TriggerEvent { get; init; } = string.Empty;
    public Dictionary<string, string> Conditions { get; init; } = new();
    public List<WorkflowActionResponse> Actions { get; init; } = new();

    public static WorkflowResponse From(WorkflowDefinition w) => new()
    {
        Id = w.Id,
        Name = w.Name,
        TriggerContentType = w.TriggerContentType,
        TriggerContentTypes = WorkflowTriggers.ContentTypes(w),
        TriggerEvent = w.TriggerEvent,
        Conditions = w.Conditions,
        Actions = w.Actions.Select(WorkflowActionResponse.From).ToList(),
    };
}

/// <summary>An action with its secret replaced by whether there is one.</summary>
/// <remarks>
/// The stored value is ciphertext, so returning it would not hand out the secret, but a response
/// shape with nowhere to put it cannot be made to do that by a later change that forgets why. Same
/// reasoning as <c>EmailSettingsResponse.ApiKeySet</c>.
/// </remarks>
internal sealed class WorkflowActionResponse
{
    public string Type { get; init; } = string.Empty;
    public Dictionary<string, string> Parameters { get; init; } = new();
    public bool SecretSet { get; init; }

    public static WorkflowActionResponse From(WorkflowAction a) => new()
    {
        Type = a.Type,
        Parameters = WebhookSigning.WithoutSecret(a.Parameters),
        SecretSet = WebhookSigning.HasSecret(a.Parameters),
    };
}
