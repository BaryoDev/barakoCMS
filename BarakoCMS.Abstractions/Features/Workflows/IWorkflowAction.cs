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
    [Obsolete("Implement RunAsync instead, which reports whether the action succeeded. "
        + "ExecuteAsync is removed in barakoCMS 5.0.")]
    Task ExecuteAsync(Dictionary<string, string> parameters, Models.Content content, CancellationToken ct);

    /// <summary>
    /// Execute the action and report whether it did what it was configured to do.
    /// </summary>
    /// <param name="parameters">Action-specific parameters from the workflow definition.</param>
    /// <param name="content">The content that triggered the workflow.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The outcome the engine records against the workflow run.</returns>
    /// <remarks>
    /// The default implementation delegates to <c>ExecuteAsync</c> and reports success, so an action
    /// written against the older contract keeps working unchanged and reports failure the only way it
    /// can: by throwing, which the engine records. Override this to report a failure that is an
    /// expected outcome rather than a defect.
    /// </remarks>
    async Task<WorkflowActionResult> RunAsync(Dictionary<string, string> parameters, Models.Content content, CancellationToken ct)
    {
#pragma warning disable CS0618 // the delegation is the point of the deprecation window
        await ExecuteAsync(parameters, content, ct);
#pragma warning restore CS0618
        return WorkflowActionResult.Success();
    }
}
