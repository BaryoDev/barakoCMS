namespace barakoCMS.Features.Workflows;

/// <summary>
/// Contract for workflow action plugins.
/// Implement this interface to create custom actions (e.g., Webhook, Slack, Discord).
/// </summary>
public interface IWorkflowAction
{
    /// <summary>
    /// The unique type identifier for this action (e.g., "Email", "SMS", "Webhook").
    /// Must match the <see cref="Models.WorkflowAction.Type"/> in workflow definitions.
    /// </summary>
    string Type { get; }

    /// <summary>
    /// Execute the action with the given parameters and content context.
    /// </summary>
    /// <param name="parameters">Action-specific parameters from the workflow definition.</param>
    /// <param name="content">The content that triggered the workflow.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ExecuteAsync(Dictionary<string, string> parameters, Models.Content content, CancellationToken ct);
}
