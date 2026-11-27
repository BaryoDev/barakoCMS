namespace barakoCMS.Core.Expressions;

/// <summary>
/// The formula language for computed fields: a closed grammar, a fixed function list, no code.
/// Names resolve only through the resolvers handed in, and a function name only through
/// <see cref="FunctionTable"/>. <c>ArchitectureTests</c> keeps reflection, pattern matching engines,
/// expression trees and scripting out of this folder.
/// </summary>
/// <remarks>
/// Positions are 0-based character offsets into the source. Errors and notes name the rule and the
/// position and never quote an entry value or the text of a string literal.
/// </remarks>
internal static class ExpressionEngine
{
    /// <summary>
    /// Parses a formula and checks every name and type. <paramref name="refuseClock"/> refuses
    /// <c>today()</c> and <c>now()</c>, for a stored computed field whose value would go stale.
    /// </summary>
    public static ParseResult ParseAndCheck(string? source, NameResolver names, bool refuseClock, ExpressionLimits? limits = null)
    {
        limits ??= ExpressionLimits.Default;
        if (string.IsNullOrWhiteSpace(source))
            return Refused(new ExpressionError(Rules.Empty, 0, "the formula is empty"));

        if (source.Length > limits.MaxSource)
            return Refused(new ExpressionError(Rules.SourceTooLong, limits.MaxSource, $"a formula may be at most {limits.MaxSource} characters"));

        var tokens = new List<Token>();
        if (Lexer.TryLex(source, tokens) is { } lexError)
            return Refused(lexError);

        var tree = Parser.Parse(tokens, limits);
        if (tree.Error is not null)
            return Refused(tree.Error);

        if (tree.Nodes > limits.MaxSteps)
            return Refused(new ExpressionError(Rules.StepBudget, 0, $"a formula may take at most {limits.MaxSteps} steps"));

        var checker = new TypeChecker(names, refuseClock);
        var type = checker.Check(tree.Root!);
        if (checker.Errors.Count > 0 || type is null)
            return new ParseResult(null, checker.Errors);

        var expression = new CheckedExpression(tree.Root!, type.Value, tree.Nodes, source.Length, checker.FieldsUsed, checker.UsesClock, checker.SystemUsed);
        return new ParseResult(expression, []);
    }

    /// <summary>
    /// Evaluates a checked formula. The result is a decimal, string, bool or DateTime, or null with
    /// an optional note saying why. The bounds are checked again here.
    /// </summary>
    public static EvalResult Evaluate(CheckedExpression expression, ValueResolver values, TimeProvider clock, ExpressionLimits? limits = null) =>
        new Evaluator(limits ?? ExpressionLimits.Default, values, clock).Run(expression);

    /// <summary>
    /// The field names and the system names a formula reads, found without a schema, or null when it
    /// does not parse. For readers that only need to know what a stored formula touches, such as who
    /// may read it. Names are given as written, once each.
    /// </summary>
    public static (IReadOnlyList<string> Fields, IReadOnlyList<string> System)? Names(string? source, ExpressionLimits? limits = null)
    {
        limits ??= ExpressionLimits.Default;
        if (string.IsNullOrWhiteSpace(source) || source.Length > limits.MaxSource)
            return null;

        var tokens = new List<Token>();
        if (Lexer.TryLex(source, tokens) is not null)
            return null;

        var tree = Parser.Parse(tokens, limits);
        if (tree.Error is not null || tree.Root is null)
            return null;

        var fields = new List<string>();
        var system = new List<string>();
        var pending = new Stack<Node>();
        pending.Push(tree.Root);
        while (pending.Count > 0)
        {
            switch (pending.Pop())
            {
                case NameNode name:
                    var into = name.IsSystem ? system : fields;
                    if (!into.Contains(name.Name, StringComparer.OrdinalIgnoreCase))
                        into.Add(name.Name);
                    break;
                case UnaryNode unary:
                    pending.Push(unary.Operand);
                    break;
                case BinaryNode binary:
                    pending.Push(binary.Right);
                    pending.Push(binary.Left);
                    break;
                case ChainNode chain:
                    foreach (var operand in chain.Operands.Reverse())
                        pending.Push(operand);
                    break;
                case CallNode call:
                    foreach (var arg in call.Args.Reverse())
                        pending.Push(arg);
                    break;
            }
        }

        return (fields, system);
    }

    private static ParseResult Refused(ExpressionError error) => new(null, [error]);
}
