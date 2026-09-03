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
    private WorkflowActionResult(bool succeeded, string? error, bool retryable)
    {
        Succeeded = succeeded;
        Error = error;
        Retryable = retryable;
    }

    /// <summary>
    /// Whether trying again could produce a different answer.
    /// </summary>
    /// <remarks>
    /// A provider answering 503 is worth retrying. A malformed URL, an unknown action type or a
    /// template naming a field that may not leave are not: they are the same on the fifth attempt as
    /// on the first, and retrying them spends ten minutes of backoff before an operator is told
    /// something they could have fixed immediately. Worse, it is load a third party did not ask for
    /// on account of a typo.
    /// </remarks>
    public bool Retryable { get; } = true;

    /// <summary>Whether the action did what it was configured to do.</summary>
    public bool Succeeded { get; }

    /// <summary>Why the action failed, or null when it succeeded.</summary>
    public string? Error { get; }

    /// <summary>The action completed.</summary>
    public static WorkflowActionResult Success() => new(true, null, retryable: false);

    /// <summary>The action did not complete, for the stated reason.</summary>
    /// <param name="error">
    /// What went wrong, in terms an operator reading the run record can act on. It is stored and
    /// served over the API, so it must not carry credentials or personal data.
    /// </param>
    public static WorkflowActionResult Failure(string error) => new(false, error, retryable: true);

    /// <summary>
    /// The action cannot complete, and trying again will not change that.
    /// </summary>
    /// <param name="error">
    /// What is wrong with the configuration, in terms the operator who wrote it can act on. Stored
    /// and served over the API, so it must not carry credentials or personal data.
    /// </param>
    /// <remarks>
    /// The runner marks this Failed immediately rather than backing off through the attempt budget.
    /// A configuration error is not a transient one, and the operator learning about it in ten
    /// minutes instead of now helps nobody.
    /// </remarks>
    public static WorkflowActionResult PermanentFailure(string error) => new(false, error, retryable: false);
}
