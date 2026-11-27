namespace barakoCMS.Core.Expressions;

/// <summary>
/// Walks a checked formula over the values a resolver gives. Nothing here throws on data: a division
/// by zero, an overflow or a value of the wrong type gives null and a note. A bound that is hit
/// (text too long, step budget, depth) gives null for the whole formula, so a null cannot be
/// swallowed by <c>coalesce</c> into a different answer.
/// </summary>
internal sealed class Evaluator(ExpressionLimits limits, ValueResolver values, TimeProvider clock)
{
    private int _steps;
    private ExpressionError? _note;

    private sealed class Abort(ExpressionError error) : Exception
    {
        public ExpressionError Error { get; } = error;
    }

    public TimeProvider Clock => clock;

    public ExpressionLimits Limits => limits;

    public EvalResult Run(CheckedExpression expression)
    {
        if (BoundBroken(expression) is { } broken)
            return new EvalResult(null, broken);

        try
        {
            var value = Eval(expression.Root, 1);
            if (value is string text)
                Bound(text.Length, expression.Root.Position);

            return new EvalResult(value, _note);
        }
        catch (Abort abort)
        {
            return new EvalResult(null, abort.Error);
        }
    }

    private ExpressionError? BoundBroken(CheckedExpression e)
    {
        if (e.SourceLength > limits.MaxSource)
            return new ExpressionError(Rules.SourceTooLong, 0, $"a formula may be at most {limits.MaxSource} characters");

        if (e.NodeCount > limits.MaxNodes)
            return new ExpressionError(Rules.NodeLimit, 0, $"a formula may have at most {limits.MaxNodes} parts");

        if (e.Root.Height > limits.MaxDepth)
            return new ExpressionError(Rules.DepthLimit, 0, $"a formula may be at most {limits.MaxDepth} levels deep");

        return e.NodeCount > limits.MaxSteps
            ? new ExpressionError(Rules.StepBudget, 0, $"a formula may take at most {limits.MaxSteps} steps")
            : null;
    }

    /// <summary>Records a note and gives null, for a function to return when its arguments are out of range.</summary>
    public object? Local(string rule, int position, string message)
    {
        _note ??= new ExpressionError(rule, position, message);
        return null;
    }

    /// <summary>Counts work done on text: one step for each 32 characters.</summary>
    public void Charge(int characters, int position) => Step(characters / 32, position);

    /// <summary>Refuses the whole evaluation when text would pass the length bound.</summary>
    public void Bound(long length, int position)
    {
        if (length > limits.MaxText)
            throw new Abort(new ExpressionError(Rules.TextTooLong, position, $"text may be at most {limits.MaxText} characters"));
    }

    public void Reserve(int length, int position)
    {
        Bound(length, position);
        Charge(length, position);
    }

    private void Step(int cost, int position)
    {
        _steps += cost;
        if (_steps > limits.MaxSteps)
            throw new Abort(new ExpressionError(Rules.StepBudget, position, $"a formula may take at most {limits.MaxSteps} steps"));
    }

    private object? Eval(Node node, int depth)
    {
        if (depth > limits.MaxDepth)
            throw new Abort(new ExpressionError(Rules.DepthLimit, node.Position, $"a formula may be at most {limits.MaxDepth} levels deep"));

        Step(1, node.Position);
        return node switch
        {
            Literal literal => literal.Value,
            NameNode name => Read(name),
            UnaryNode unary => EvalUnary(unary, depth),
            ChainNode chain => EvalChain(chain, depth),
            BinaryNode binary => EvalBinary(binary, depth),
            CallNode call => EvalCall(call, depth),
            _ => throw new Abort(new ExpressionError(Rules.Syntax, node.Position, "this part cannot be evaluated")),
        };
    }

    private object? Read(NameNode name)
    {
        var raw = values(new NameRef(name.Name, name.IsSystem));
        if (!Values.TryNormalize(raw, out var value) || (value is not null && Values.TypeOf(value) != name.Type))
            return Local(Rules.ValueType, name.Position, "a stored value does not fit the type the formula expects");

        return value;
    }

    private object? EvalUnary(UnaryNode unary, int depth)
    {
        var operand = Eval(unary.Operand, depth + 1);
        if (operand is null)
            return null;

        return unary.Op == "-" ? -(decimal)operand : !(bool)operand;
    }

    private object? EvalBinary(BinaryNode node, int depth)
    {
        var left = Eval(node.Left, depth + 1);
        var right = Eval(node.Right, depth + 1);
        return Comparison(node, left, right);
    }

    /// <summary>Walks a flat run left to right in a loop, so a long chain does not recurse.</summary>
    private object? EvalChain(ChainNode node, int depth)
    {
        if (node.Ops[0] is "and" or "or")
            return EvalLogic(node, depth);

        var acc = Eval(node.Operands[0], depth + 1);
        for (var i = 0; i < node.Ops.Count; i++)
        {
            var right = Eval(node.Operands[i + 1], depth + 1);
            acc = Arithmetic(node.Ops[i], node.OpPositions[i], acc, right);
        }

        return acc;
    }

    private object? EvalLogic(ChainNode node, int depth)
    {
        var decisive = node.Ops[0] == "or";
        var acc = (bool?)Eval(node.Operands[0], depth + 1);
        for (var i = 1; i < node.Operands.Count; i++)
        {
            if (acc == decisive)
                return decisive;

            var right = (bool?)Eval(node.Operands[i], depth + 1);
            if (right == decisive)
                return decisive;

            acc = acc is null || right is null ? null : !decisive;
        }

        return acc;
    }

    private object? Arithmetic(string op, int position, object? left, object? right)
    {
        if (left is null || right is null)
            return null;

        decimal a = (decimal)left, b = (decimal)right;
        try
        {
            switch (op)
            {
                case "+":
                    return a + b;
                case "-":
                    return a - b;
                case "*":
                    return a * b;
                default:
                    return b == 0m ? Local(Rules.DivisionByZero, position, "division by zero gives null") : a / b;
            }
        }
        catch (OverflowException)
        {
            return Local(Rules.Overflow, position, "the number is out of range, so the result is null");
        }
    }

    private object? Comparison(BinaryNode node, object? left, object? right)
    {
        if (node.Op is "=" or "!=")
        {
            var equal = left is null || right is null ? left is null && right is null : Compare(left, right, node.Position) == 0;
            return node.Op == "=" ? equal : !equal;
        }

        if (left is null || right is null)
            return null;

        var order = Compare(left, right, node.Position);
        return node.Op switch
        {
            "<" => order < 0,
            "<=" => order <= 0,
            ">" => order > 0,
            _ => order >= 0,
        };
    }

    private int Compare(object left, object right, int position)
    {
        switch (left)
        {
            case decimal a:
                return a.CompareTo((decimal)right);
            case string a:
                Charge(a.Length + ((string)right).Length, position);
                return string.CompareOrdinal(a, (string)right);
            case DateTime a:
                return a.CompareTo((DateTime)right);
            default:
                return ((bool)left).CompareTo((bool)right);
        }
    }

    private object? EvalCall(CallNode call, int depth)
    {
        var def = FunctionTable.Find(call.Name)!;
        return def.Name switch
        {
            "iif" => Eval(call.Args[(bool?)Eval(call.Args[0], depth + 1) == true ? 1 : 2], depth + 1),
            "switch" => EvalSwitch(call, depth),
            "coalesce" => EvalCoalesce(call, depth),
            _ => Invoke(call, def, depth),
        };
    }

    private object? EvalSwitch(CallNode call, int depth)
    {
        for (var i = 0; i + 1 < call.Args.Count; i += 2)
        {
            if ((bool?)Eval(call.Args[i], depth + 1) == true)
                return Eval(call.Args[i + 1], depth + 1);
        }

        return Eval(call.Args[^1], depth + 1);
    }

    private object? EvalCoalesce(CallNode call, int depth)
    {
        foreach (var arg in call.Args)
        {
            var value = Eval(arg, depth + 1);
            if (!Values.IsBlank(value))
                return value;
        }

        return null;
    }

    private object? Invoke(CallNode call, FunctionDef def, int depth)
    {
        var args = new object?[call.Args.Count];
        for (var i = 0; i < args.Length; i++)
            args[i] = Eval(call.Args[i], depth + 1);

        if (def.PropagatesNull && args.Any(a => a is null))
            return null;

        return def.Impl!(this, call, args);
    }
}
