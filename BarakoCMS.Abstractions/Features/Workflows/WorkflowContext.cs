namespace barakoCMS.Features.Workflows;

/// <summary>What the runner hands one action of a run.</summary>
public sealed class WorkflowContext
{
    private static readonly IReadOnlyDictionary<int, IReadOnlyDictionary<string, string>> NoOutputs =
        new Dictionary<int, IReadOnlyDictionary<string, string>>();

    /// <summary>The action's parameters with their placeholders resolved.</summary>
    public required Dictionary<string, string> Parameters { get; init; }

    /// <summary>The entry that triggered the run. On a Deleted trigger only its id and content type are real.</summary>
    public required Models.Content Content { get; init; }

    /// <summary>The trigger that fired: a content event, a transition or a module's trigger.</summary>
    public required string TriggerEvent { get; init; }

    /// <summary>
    /// This action's position in the run, counting from zero, or -1 when the caller did not say.
    /// </summary>
    public int Ordinal { get; init; } = -1;

    /// <summary>The tenant the run belongs to.</summary>
    public string TenantId { get; init; } = string.Empty;

    /// <summary>
    /// What earlier actions of this run produced and the workflow captured, by the action's
    /// position counting from zero, then by name.
    /// </summary>
    /// <remarks>
    /// Only captured outputs of actions that succeeded are here. An action that has not run yet,
    /// failed, or produced nothing the workflow captured has no entry.
    /// </remarks>
    public IReadOnlyDictionary<int, IReadOnlyDictionary<string, string>> Outputs { get; init; } = NoOutputs;

    /// <summary>
    /// The stored files earlier actions of this run declared they made
    /// (<see cref="WorkflowActionResult.ProducedFiles"/>), by the action's position counting from
    /// zero.
    /// </summary>
    /// <remarks>
    /// Only actions that succeeded are here, and only files of this run. An id that appears in an
    /// output or a parameter but not here was not made by this run.
    /// </remarks>
    public IReadOnlyDictionary<int, IReadOnlyList<Guid>> ProducedFiles { get; init; } = NoFiles;

    private static readonly IReadOnlyDictionary<int, IReadOnlyList<Guid>> NoFiles =
        new Dictionary<int, IReadOnlyList<Guid>>();
}
