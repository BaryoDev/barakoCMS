namespace barakoCMS.Features.Workflows;

/// <summary>
/// The outcome of a single workflow action.
/// </summary>
/// <remarks>
/// A webhook answering 500, an email provider rejecting a recipient, a missing parameter: these are
/// expected outcomes of a configured action, not defects, so they are values rather than exceptions.
/// An action that throws is still a failure; the engine converts it to <see cref="Failure"/> when it
/// records the run.
/// </remarks>
public sealed record WorkflowActionResult
{
    private WorkflowActionResult(bool succeeded, string? error)
    {
        Succeeded = succeeded;
        Error = error;
    }

    /// <summary>Whether the action did what it was configured to do.</summary>
    public bool Succeeded { get; }

    /// <summary>Why the action failed, or null when it succeeded.</summary>
    public string? Error { get; }

    /// <summary>The action completed.</summary>
    public static WorkflowActionResult Success() => new(true, null);

    /// <summary>The action did not complete, for the stated reason.</summary>
    /// <param name="error">
    /// What went wrong, in terms an operator reading the run record can act on. It is stored and
    /// served over the API, so it must not carry credentials or personal data.
    /// </param>
    public static WorkflowActionResult Failure(string error) => new(false, error);
}
