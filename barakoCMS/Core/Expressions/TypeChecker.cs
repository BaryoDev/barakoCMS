namespace barakoCMS.Core.Expressions;

/// <summary>
/// Checks every name, function and operand against the schema and the function table. It reports
/// every problem it finds, and returns null for a part it could not type so one mistake does not
/// raise a string of follow-on errors. A null literal fits any type.
/// </summary>
internal sealed class TypeChecker(NameResolver names, bool refuseClock)
{
    private readonly HashSet<string> _used = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _usedInOrder = [];

    public List<ExpressionError> Errors { get; } = [];

    /// <summary>The fields the formula reads, as first written.</summary>
    public IReadOnlyList<string> FieldsUsed => _usedInOrder;

    public bool UsesClock { get; private set; }

    public ExprType? Check(Node node) => node switch
    {
        Literal literal => literal.Type,
        NameNode name => CheckName(name),
        UnaryNode unary => CheckUnary(unary),
        BinaryNode binary => CheckBinary(binary),
        CallNode call => CheckCall(call),
        _ => null,
    };

    private ExprType? Report(string rule, int position, string message)
    {
        Errors.Add(new ExpressionError(rule, position, message));
        return null;
    }

    private static bool Fits(ExprType? type, ExprType wanted) => type is null || type == ExprType.Null || type == wanted;

    private static ExprType? Unify(ExprType a, ExprType b)
    {
        if (a == b || b == ExprType.Null)
            return a;

        return a == ExprType.Null ? b : null;
    }

    private ExprType? CheckName(NameNode name)
    {
        if (name.IsSystem)
        {
            name.Type = name.Name is "createdAt" or "updatedAt" ? ExprType.Date : ExprType.Text;
            return name.Type;
        }

        if (names(name.Name) is not { } type || type == ExprType.Null)
            return Report(Rules.UnknownName, name.Position, $"the type has no field '{name.Name}' that a formula can read");

        if (_used.Add(name.Name))
            _usedInOrder.Add(name.Name);

        name.Type = type;
        return type;
    }

    private ExprType? CheckUnary(UnaryNode unary)
    {
        var wanted = unary.Op == "-" ? ExprType.Number : ExprType.Bool;
        var operand = Check(unary.Operand);
        if (!Fits(operand, wanted))
            Report(Rules.TypeMismatch, unary.Position, $"'{unary.Op}' takes {Values.Word(wanted)}, and this is {Values.Word(operand!.Value)}");

        return wanted;
    }

    private ExprType? CheckBinary(BinaryNode node)
    {
        var left = Check(node.Left);
        var right = Check(node.Right);
        var (result, operands) = node.Op switch
        {
            "+" or "-" or "*" or "/" => (ExprType.Number, ExprType.Number),
            "and" or "or" => (ExprType.Bool, ExprType.Bool),
            _ => (ExprType.Bool, ExprType.Null),
        };

        if (left is null || right is null)
            return result;

        if (operands != ExprType.Null)
        {
            if (!Fits(left, operands) || !Fits(right, operands))
                Report(Rules.TypeMismatch, node.Position, $"'{node.Op}' takes {Values.Word(operands)} on both sides, and this has {Values.Word(left.Value)} and {Values.Word(right.Value)}");

            return result;
        }

        var shared = Unify(left.Value, right.Value);
        if (shared is null)
            Report(Rules.TypeMismatch, node.Position, $"'{node.Op}' compares like with like, and this has {Values.Word(left.Value)} and {Values.Word(right.Value)}");
        else if (node.Op is not ("=" or "!=") && shared is ExprType.Bool)
            Report(Rules.TypeMismatch, node.Position, $"'{node.Op}' orders numbers, text and dates, not bool");

        return result;
    }

    private ExprType? CheckCall(CallNode call)
    {
        var types = call.Args.Select(Check).ToList();
        var def = FunctionTable.Find(call.Name);
        if (def is null)
            return Report(Rules.UnknownFunction, call.Position, $"'{call.Name}' is not a function; the list is fixed");

        if (call.Args.Count < def.Min || (def.Max >= 0 && call.Args.Count > def.Max))
            return Report(Rules.Arity, call.Position, $"{def.Name} takes {ArityText(def)} arguments, and {call.Args.Count} were given");

        if (DateFunctions.IsClock(def.Name))
        {
            UsesClock = true;
            if (refuseClock)
                Report(Rules.ClockFunction, call.Position, $"{def.Name}() reads the clock, which a stored computed field may not, because its value would go stale");

            return ExprType.Date;
        }

        return def.Name switch
        {
            "iif" => CheckIif(call, types),
            "switch" => CheckSwitch(call, types),
            "coalesce" => CheckUnified(call, types, Enumerable.Range(0, types.Count)),
            "format" => CheckFormat(call, types),
            "round" => CheckRound(call, def, types),
            _ => Typed(call, def, types),
        };
    }

    private static string ArityText(FunctionDef def) =>
        def.Max == def.Min ? $"{def.Min}" : def.Max < 0 ? $"at least {def.Min}" : $"{def.Min} to {def.Max}";

    private static bool Matches(Arg wanted, ExprType type) => wanted switch
    {
        Arg.Any => true,
        Arg.Number => type == ExprType.Number,
        Arg.Text => type == ExprType.Text,
        Arg.Bool => type == ExprType.Bool,
        _ => type == ExprType.Date,
    };

    private ExprType Typed(CallNode call, FunctionDef def, List<ExprType?> types)
    {
        for (var i = 0; i < types.Count; i++)
        {
            var wanted = def.ParamAt(i);
            if (types[i] is not { } type || type == ExprType.Null || Matches(wanted, type))
                continue;

            Report(Rules.TypeMismatch, call.Args[i].Position,
                $"{def.Name} argument {i + 1} takes {wanted.ToString().ToLowerInvariant()}, and this is {Values.Word(type)}");
        }

        return def.Result;
    }

    private ExprType? CheckRound(CallNode call, FunctionDef def, List<ExprType?> types)
    {
        Typed(call, def, types);
        if (call.Args.Count == 2 && call.Args[1] is Literal { Value: decimal places } && !MathFunctions.IsPlaces(places))
            Report(Rules.ArgumentRange, call.Args[1].Position, "round takes whole places from 0 to 10");

        return ExprType.Number;
    }

    private ExprType? CheckIif(CallNode call, List<ExprType?> types)
    {
        if (!Fits(types[0], ExprType.Bool))
            Report(Rules.TypeMismatch, call.Args[0].Position, "iif takes a bool condition first");

        return CheckUnified(call, types, [1, 2]);
    }

    private ExprType? CheckSwitch(CallNode call, List<ExprType?> types)
    {
        if (types.Count % 2 == 0)
            return Report(Rules.Arity, call.Position, "switch takes pairs of condition and value, then a default");

        for (var i = 0; i + 1 < types.Count; i += 2)
        {
            if (!Fits(types[i], ExprType.Bool))
                Report(Rules.TypeMismatch, call.Args[i].Position, "switch takes a bool condition before each value");
        }

        return CheckUnified(call, types, Enumerable.Range(0, types.Count).Where(i => i % 2 == 1 || i == types.Count - 1));
    }

    /// <summary>The listed arguments are alternatives for one result, so they must share a type.</summary>
    private ExprType? CheckUnified(CallNode call, List<ExprType?> types, IEnumerable<int> which)
    {
        ExprType? shared = ExprType.Null;
        foreach (var i in which)
        {
            if (types[i] is not { } type || shared is null)
                continue;

            shared = Unify(shared.Value, type);
            if (shared is null)
                return Report(Rules.TypeMismatch, call.Args[i].Position, $"{call.Name} gives one type, and this argument is {Values.Word(type)}, which does not match the others");
        }

        return shared;
    }

    private ExprType? CheckFormat(CallNode call, List<ExprType?> types)
    {
        if (call.Args[1] is not Literal { Value: string pattern })
            return Report(Rules.FormatPattern, call.Args[1].Position, "format takes a quoted pattern from the fixed list");

        if (!Fits(types[0], ExprType.Number) && !Fits(types[0], ExprType.Date))
            return Report(Rules.TypeMismatch, call.Args[0].Position, "format takes a number or a date");

        var onList = types[0] switch
        {
            ExprType.Number => FormatPatterns.IsNumber(pattern),
            ExprType.Date => FormatPatterns.IsDate(pattern),
            _ => FormatPatterns.IsNumber(pattern) || FormatPatterns.IsDate(pattern),
        };

        if (!onList)
        {
            Report(Rules.FormatPattern, call.Args[1].Position,
                $"this pattern is not on the list. Numbers: {string.Join(", ", FormatPatterns.Numbers)}. Dates: {string.Join(", ", FormatPatterns.Dates)}");
        }

        return ExprType.Text;
    }
}
