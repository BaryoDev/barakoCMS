namespace barakoCMS.Core.Expressions;

internal static class MathFunctions
{
    /// <summary>The most places <c>round</c> keeps. Computed fields read it from here.</summary>
    public const int MaxRound = 10;

    public static bool IsPlaces(decimal places) =>
        places == Math.Truncate(places) && places >= 0 && places <= MaxRound;

    public static void Register(Action<FunctionDef> add)
    {
        add(new("round", 1, 2, [Arg.Number], ExprType.Number, Round));
        add(new("floor", 1, 1, [Arg.Number], ExprType.Number, (_, _, a) => Math.Floor((decimal)a[0]!)));
        add(new("ceiling", 1, 1, [Arg.Number], ExprType.Number, (_, _, a) => Math.Ceiling((decimal)a[0]!)));
        add(new("abs", 1, 1, [Arg.Number], ExprType.Number, (_, _, a) => Math.Abs((decimal)a[0]!)));
        add(new("min", 2, -1, [Arg.Number], ExprType.Number, (_, _, a) => a.Min(x => (decimal)x!)));
        add(new("max", 2, -1, [Arg.Number], ExprType.Number, (_, _, a) => a.Max(x => (decimal)x!)));
    }

    private static object? Round(Evaluator ev, CallNode call, object?[] a)
    {
        var places = a.Length > 1 ? (decimal)a[1]! : 0m;
        if (!IsPlaces(places))
            return ev.Local(Rules.ArgumentRange, call.Position, $"round takes places from 0 to {MaxRound}");

        return Math.Round((decimal)a[0]!, (int)places, MidpointRounding.AwayFromZero);
    }
}
