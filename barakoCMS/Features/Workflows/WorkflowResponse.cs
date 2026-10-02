using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Models;

namespace barakoCMS.Features.Workflows;

/// <summary>A workflow as the API describes it, rather than as it is stored.</summary>
/// <remarks>
/// See <c>Features/Roles/RoleResponse</c> for the reasoning. <see cref="CreateWorkflowRequest"/> is
/// the same separation on the way in.
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

    /// <summary>Every event the workflow fires on, including <see cref="TriggerEvent"/>.</summary>
    public List<string> TriggerEvents { get; init; } = new();
    public Dictionary<string, string> Conditions { get; init; } = new();
    public List<WorkflowActionResponse> Actions { get; init; } = new();

    /// <summary>Whether the workflow fires. True for a workflow saved before it could be switched off.</summary>
    public bool Enabled { get; init; } = true;

    public static WorkflowResponse From(WorkflowDefinition w) => new()
    {
        Id = w.Id,
        Name = w.Name,
        TriggerContentType = w.TriggerContentType,
        TriggerContentTypes = WorkflowTriggers.ContentTypes(w),
        TriggerEvent = w.TriggerEvent,
        TriggerEvents = WorkflowTriggers.Events(w),
        Conditions = w.Conditions,
        Actions = w.Actions.Select(WorkflowActionResponse.From).ToList(),
        Enabled = w.Enabled,
    };
}

/// <summary>An action with its secret replaced by whether there is one.</summary>
/// <remarks>
/// The stored value is ciphertext, so returning it would not hand out the secret, but a response
/// shape with nowhere to put it cannot be made to do that by a later change that forgets why. Same
/// reasoning as <c>EmailSettingsResponse.ApiKeySet</c>.
///
/// The children a Conditional carries in <c>ThenActions</c> and <c>ElseActions</c> get the same
/// treatment inside that JSON: credential-named keys are left out, on the child and in its
/// parameters, and each child gains a
/// <c>SecretSet</c> of its own. A branch that cannot be read that way is not returned at all, since
/// nothing has looked inside it.
/// </remarks>
internal sealed class WorkflowActionResponse
{
    public string Type { get; init; } = string.Empty;
    public Dictionary<string, string> Parameters { get; init; } = new();
    public bool SecretSet { get; init; }

    /// <summary>Continue or Halt. Continue for an action saved without the setting, or with null.</summary>
    public WorkflowFailurePolicy OnFailure { get; init; }

    /// <summary>
    /// The branches of a Conditional left out of <see cref="Parameters"/> because they are not a
    /// JSON array of actions the Conditional can run (see
    /// <see cref="WebhookSigning.IsReadableBranch"/>). Such a branch never runs.
    /// </summary>
    public List<string> UnreadableBranches { get; init; } = new();

    public static WorkflowActionResponse From(WorkflowAction a)
    {
        var parameters = WebhookSigning.WithoutSecret(a.Type, a.Parameters, out var unreadableBranches);

        return new()
        {
            Type = a.Type,
            Parameters = parameters,
            SecretSet = WebhookSigning.HasSecret(a.Parameters),
            OnFailure = a.OnFailure ?? WorkflowFailurePolicy.Continue,
            UnreadableBranches = unreadableBranches,
        };
    }
}
