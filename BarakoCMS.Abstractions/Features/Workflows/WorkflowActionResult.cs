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
    private static readonly IReadOnlyDictionary<string, string> NoOutputs = new Dictionary<string, string>();

    private WorkflowActionResult(bool succeeded, string? error, bool retryable, IReadOnlyDictionary<string, string>? outputs = null)
    {
        Succeeded = succeeded;
        Error = error;
        Retryable = retryable;
        Outputs = outputs ?? NoOutputs;
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

    /// <summary>
    /// The HTTP status the action's remote call answered with, when it made one and got an answer.
    /// Kept on the run's attempt history. Null when the action made no such call or got no answer.
    /// </summary>
    public int? ResponseStatus { get; init; }

    /// <summary>Whether the action did what it was configured to do.</summary>
    public bool Succeeded { get; }

    /// <summary>Why the action failed, or null when it succeeded.</summary>
    public string? Error { get; }

    /// <summary>
    /// What the action produced, by name, for the actions after it. Empty unless it succeeded with
    /// outputs.
    /// </summary>
    /// <remarks>
    /// Only the names the workflow lists in the action's <c>Capture</c> are kept on the run, and
    /// only those can be read by a later action as <c>{{actions.N.name}}</c>. Everything else is
    /// dropped when the outcome is recorded, so a provider's whole response never reaches the run
    /// record, which is stored and served over the API.
    /// </remarks>
    public IReadOnlyDictionary<string, string> Outputs { get; }

    /// <summary>
    /// The ids of stored files this action made on this run, such as a rendered document. Empty
    /// unless the action sets it.
    /// </summary>
    /// <remarks>
    /// The runner records them on the attempt, whether or not the workflow captures any output. An
    /// Email action later in the same run may attach one, even a private one, because the run made
    /// it. Declare only a file this call created or found by a key of its own, never an id taken
    /// from a parameter or an entry: that would let a workflow author mail any file. Past
    /// <see cref="MaxProducedFiles"/> the rest are not kept.
    /// </remarks>
    public IReadOnlyList<Guid> ProducedFiles { get; init; } = [];

    /// <summary>How many produced files one action may declare.</summary>
    public const int MaxProducedFiles = 20;
    /// <summary>The entries the action created, or null when it reported none.</summary>
    public IReadOnlyList<barakoCMS.Models.CreatedRef>? Created { get; private init; }

    /// <summary>The action completed.</summary>
    public static WorkflowActionResult Success() => new(true, null, retryable: false);

    /// <summary>The action completed and produced these values.</summary>
    /// <param name="outputs">
    /// Values by name. A later action reads one the workflow captured as <c>{{actions.N.name}}</c>,
    /// where N is this action's position, counting from zero.
    /// </param>
    public static WorkflowActionResult Success(IReadOnlyDictionary<string, string> outputs) =>
        new(true, null, retryable: false, new Dictionary<string, string>(outputs ?? NoOutputs, StringComparer.Ordinal));

    /// <summary>The action completed, produced these values and created these entries.</summary>
    /// <param name="outputs">Values by name, as for <see cref="Success(IReadOnlyDictionary{string, string})"/>, or null for none.</param>
    /// <param name="created">The entries the action created, kept on the attempt once it succeeds.</param>
    public static WorkflowActionResult Success(IReadOnlyDictionary<string, string>? outputs, IReadOnlyList<barakoCMS.Models.CreatedRef> created) =>
        new WorkflowActionResult(true, null, retryable: false,
            outputs is null ? null : new Dictionary<string, string>(outputs, StringComparer.Ordinal)) { Created = [.. created] };

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
