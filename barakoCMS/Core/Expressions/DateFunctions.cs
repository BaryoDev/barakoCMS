namespace barakoCMS.Core.Expressions;

internal static class DateFunctions
{
    private const decimal MaxDays = 3_652_059m;
    private const decimal MaxMonths = 120_000m;

    public static void Register(Action<FunctionDef> add)
    {
        add(new("years", 2, 2, [Arg.Date], ExprType.Number, (_, _, a) => (decimal)DateMath.Years(Day(a, 0), Day(a, 1))));
        add(new("months", 2, 2, [Arg.Date], ExprType.Number, (_, _, a) => (decimal)DateMath.Months(Day(a, 0), Day(a, 1))));
        add(new("days", 2, 2, [Arg.Date], ExprType.Number, (_, _, a) => DateMath.Days(Day(a, 1) - Day(a, 0))));
        add(new("hours", 2, 2, [Arg.Date], ExprType.Number, (_, _, a) => DateMath.Hours(Day(a, 1) - Day(a, 0))));
        add(new("addDays", 2, 2, [Arg.Date, Arg.Number], ExprType.Date, AddDays));
        add(new("addMonths", 2, 2, [Arg.Date, Arg.Number], ExprType.Date, AddMonths));
        add(new("year", 1, 1, [Arg.Date], ExprType.Number, (_, _, a) => (decimal)Day(a, 0).Year));
        add(new("month", 1, 1, [Arg.Date], ExprType.Number, (_, _, a) => (decimal)Day(a, 0).Month));
        add(new("day", 1, 1, [Arg.Date], ExprType.Number, (_, _, a) => (decimal)Day(a, 0).Day));
        add(new("today", 0, 0, [Arg.Any], ExprType.Date, (ev, _, _) => ev.Clock.GetUtcNow().UtcDateTime.Date));
        add(new("now", 0, 0, [Arg.Any], ExprType.Date, (ev, _, _) => ev.Clock.GetUtcNow().UtcDateTime));
    }

    public static bool IsClock(string name) =>
        name.Equals("today", StringComparison.OrdinalIgnoreCase) || name.Equals("now", StringComparison.OrdinalIgnoreCase);

    private static DateTime Day(object?[] args, int index) => (DateTime)args[index]!;

    private static object? AddDays(Evaluator ev, CallNode call, object?[] a) =>
        Shift(ev, call, a, MaxDays, (d, n) => d.AddDays((double)n));

    private static object? AddMonths(Evaluator ev, CallNode call, object?[] a) =>
        Shift(ev, call, a, MaxMonths, (d, n) => d.AddMonths((int)n));

    private static object? Shift(Evaluator ev, CallNode call, object?[] a, decimal limit, Func<DateTime, decimal, DateTime> move)
    {
        var count = (decimal)a[1]!;
        if (count != Math.Truncate(count))
            return ev.Local(Rules.ArgumentRange, call.Position, $"{call.Name} takes a whole number");

        if (Math.Abs(count) > limit)
            return ev.Local(Rules.Overflow, call.Position, "the date is out of range");

        try
        {
            return move((DateTime)a[0]!, count);
        }
        catch (ArgumentOutOfRangeException)
        {
            return ev.Local(Rules.Overflow, call.Position, "the date is out of range");
        }
    }
}
