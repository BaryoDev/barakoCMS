using System.Globalization;

namespace barakoCMS.Core.Expressions;

/// <summary>
/// The fixed pattern list for <c>format</c>. A pattern is looked up here and nowhere else, so no
/// caller-supplied format string reaches a .NET formatter, and every result uses the invariant culture.
/// </summary>
internal static class FormatPatterns
{
    private readonly record struct NumberSpec(int Places, bool Group, bool Percent);

    private static readonly Dictionary<string, NumberSpec> NumberPatterns = new(StringComparer.Ordinal)
    {
        ["0"] = new(0, false, false),
        ["0.0"] = new(1, false, false),
        ["0.00"] = new(2, false, false),
        ["0.000"] = new(3, false, false),
        ["#,##0"] = new(0, true, false),
        ["#,##0.00"] = new(2, true, false),
        ["0%"] = new(0, false, true),
        ["0.0%"] = new(1, false, true),
    };

    private static readonly string[] DatePatterns =
    [
        "yyyy-MM-dd", "yyyy-MM-dd HH:mm", "dd/MM/yyyy", "MM/dd/yyyy", "d MMM yyyy", "MMMM d, yyyy", "MMM yyyy", "yyyy", "HH:mm",
    ];

    public static IReadOnlyCollection<string> Numbers => NumberPatterns.Keys;

    public static IReadOnlyCollection<string> Dates => DatePatterns;

    public static bool IsNumber(string pattern) => NumberPatterns.ContainsKey(pattern);

    public static bool IsDate(string pattern) => Array.IndexOf(DatePatterns, pattern) >= 0;

    public static void Register(Action<FunctionDef> add) =>
        add(new("format", 2, 2, [Arg.Any, Arg.Text], ExprType.Text, Format));

    private static object? Format(Evaluator ev, CallNode call, object?[] a)
    {
        var pattern = (string)a[1]!;
        if (a[0] is decimal number && NumberPatterns.TryGetValue(pattern, out var spec))
            return FormatNumber(ev, call, number, spec);

        if (a[0] is DateTime moment && IsDate(pattern))
            return moment.ToString(pattern, CultureInfo.InvariantCulture);

        return ev.Local(Rules.FormatPattern, call.Position, "the pattern is not on the list for this kind of value");
    }

    private static object? FormatNumber(Evaluator ev, CallNode call, decimal value, NumberSpec spec)
    {
        try
        {
            var scaled = spec.Percent ? value * 100m : value;
            var rounded = Math.Round(scaled, spec.Places, MidpointRounding.AwayFromZero);
            if (rounded == 0m)
                rounded = 0m;

            var code = (spec.Group ? "N" : "F") + spec.Places.ToString(CultureInfo.InvariantCulture);
            var text = rounded.ToString(code, CultureInfo.InvariantCulture);
            return spec.Percent ? text + "%" : text;
        }
        catch (OverflowException)
        {
            return ev.Local(Rules.Overflow, call.Position, "the number is out of range");
        }
    }
}
