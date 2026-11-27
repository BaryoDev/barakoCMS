namespace barakoCMS.Core.Expressions;

internal enum Arg
{
    Any,
    Number,
    Text,
    Bool,
    Date,
}

/// <summary>What a function does with arguments that are already evaluated. A null for the implementation marks a lazy function the evaluator runs itself.</summary>
internal delegate object? FunctionImpl(Evaluator ev, CallNode call, object?[] args);

/// <summary>
/// One entry of the fixed function list. <see cref="Params"/> gives the type of each argument, and
/// the last entry repeats for the rest. <see cref="Max"/> of -1 means no upper limit.
/// </summary>
internal sealed record FunctionDef(string Name, int Min, int Max, Arg[] Params, ExprType Result, FunctionImpl? Impl, bool PropagatesNull = true)
{
    public bool IsLazy => Impl is null;

    public Arg ParamAt(int index) => Params[Math.Min(index, Params.Length - 1)];
}

/// <summary>The only way a function name resolves. A name that is not here is refused.</summary>
internal static class FunctionTable
{
    private static readonly Dictionary<string, FunctionDef> All = Build();

    public static FunctionDef? Find(string name) => All.GetValueOrDefault(name);

    public static IReadOnlyCollection<string> Names => All.Values.Select(f => f.Name).Order(StringComparer.Ordinal).ToList();

    private static Dictionary<string, FunctionDef> Build()
    {
        var table = new Dictionary<string, FunctionDef>(StringComparer.OrdinalIgnoreCase);
        void Add(FunctionDef f) => table[f.Name] = f;

        MathFunctions.Register(Add);
        LogicFunctions.Register(Add);
        TextFunctions.Register(Add);
        DateFunctions.Register(Add);
        FormatPatterns.Register(Add);
        return table;
    }
}
