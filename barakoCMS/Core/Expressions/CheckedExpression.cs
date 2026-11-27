namespace barakoCMS.Core.Expressions;

/// <summary>A formula that parsed and passed the type check. Only <see cref="ExpressionEngine"/> makes one.</summary>
internal sealed class CheckedExpression
{
    internal CheckedExpression(Node root, ExprType type, int nodeCount, int sourceLength, IReadOnlyList<string> fields, bool usesClock)
    {
        Root = root;
        Type = type;
        NodeCount = nodeCount;
        SourceLength = sourceLength;
        Fields = fields;
        UsesClock = usesClock;
    }

    internal Node Root { get; }

    /// <summary>The type the formula gives. Null means the formula can only give null.</summary>
    public ExprType Type { get; }

    public int NodeCount { get; }

    public int SourceLength { get; }

    /// <summary>The fields of the entry the formula reads, once each, for cycle checks and for who may read it.</summary>
    public IReadOnlyList<string> Fields { get; }

    public bool UsesClock { get; }
}

internal sealed record ParseResult(CheckedExpression? Expression, IReadOnlyList<ExpressionError> Errors)
{
    public bool IsValid => Expression is not null;
}

/// <summary>A value (decimal, string, bool, DateTime, or null) and, when something was off, the first note about it.</summary>
internal readonly record struct EvalResult(object? Value, ExpressionError? Note);
