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
        + "ExecuteAsync is removed in barakoCMS 6.0.")]
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
    ///
    /// The <c>TriggerEvent</c> parameter names the trigger that fired. When it is <c>Deleted</c> the
    /// entry has been erased: <paramref name="content"/> carries its <c>Id</c> and <c>ContentType</c>
    /// and nothing else. Every other member is a default and not a stored value, so do not send or
    /// store its status, sensitivity, data or timestamps.
    /// </remarks>
    async Task<WorkflowActionResult> RunAsync(Dictionary<string, string> parameters, Models.Content content, CancellationToken ct)
    {
#pragma warning disable CS0618 // the delegation is the point of the deprecation window
        await ExecuteAsync(parameters, content, ct);
#pragma warning restore CS0618
        return WorkflowActionResult.Success();
    }

    /// <summary>
    /// Execute the action with everything the runner knows about the run, including what the
    /// actions before it produced.
    /// </summary>
    /// <param name="context">The resolved parameters, the content, the trigger and the earlier outputs.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The outcome, with any outputs the action produced.</returns>
    /// <remarks>
    /// The runner calls this one. The default implementation calls
    /// <see cref="RunAsync(Dictionary{string, string}, Models.Content, CancellationToken)"/> with the
    /// context's parameters and content, so an action written before it existed keeps working
    /// unchanged. Override this to read <see cref="WorkflowContext.Outputs"/>, or to return outputs
    /// with <see cref="WorkflowActionResult.Success(IReadOnlyDictionary{string, string})"/> knowing
    /// which run you are in.
    /// </remarks>
    Task<WorkflowActionResult> RunAsync(WorkflowContext context, CancellationToken ct) =>
        RunAsync(context.Parameters, context.Content, ct);
}
