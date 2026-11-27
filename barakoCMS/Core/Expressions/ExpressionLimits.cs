namespace barakoCMS.Core.Expressions;

/// <summary>
/// The bounds on a formula. Each is checked when the formula is parsed and again when it is
/// evaluated, because a stored formula can predate a bound.
/// </summary>
internal sealed record ExpressionLimits
{
    public int MaxSource { get; init; } = 2000;

    public int MaxNodes { get; init; } = 500;

    /// <summary>Both the nesting the parser recurses through and the height of the tree.</summary>
    public int MaxDepth { get; init; } = 32;

    public int MaxText { get; init; } = 10_000;

    /// <summary>Work units per evaluation: one per node, and one per 32 characters a text function reads or writes.</summary>
    public int MaxSteps { get; init; } = 2000;

    public static ExpressionLimits Default { get; } = new();
}
