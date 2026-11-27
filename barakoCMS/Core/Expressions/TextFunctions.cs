using System.Globalization;

namespace barakoCMS.Core.Expressions;

/// <summary>Text functions. Every comparison is ordinal and every case change is invariant, so no culture and no pattern is involved.</summary>
internal static class TextFunctions
{
    public static void Register(Action<FunctionDef> add)
    {
        add(new("concat", 2, -1, [Arg.Any], ExprType.Text, Concat, false));
        add(new("upper", 1, 1, [Arg.Text], ExprType.Text, (ev, c, a) => Reserved(ev, c, (string)a[0]!).ToUpperInvariant()));
        add(new("lower", 1, 1, [Arg.Text], ExprType.Text, (ev, c, a) => Reserved(ev, c, (string)a[0]!).ToLowerInvariant()));
        add(new("trim", 1, 1, [Arg.Text], ExprType.Text, Trim));
        add(new("left", 2, 2, [Arg.Text, Arg.Number], ExprType.Text, (ev, c, a) => Take(ev, c, a, fromEnd: false)));
        add(new("right", 2, 2, [Arg.Text, Arg.Number], ExprType.Text, (ev, c, a) => Take(ev, c, a, fromEnd: true)));
        add(new("len", 1, 1, [Arg.Text], ExprType.Number, (_, _, a) => (decimal)((string)a[0]!).Length));
        add(new("replace", 3, 3, [Arg.Text], ExprType.Text, Replace));
        add(new("contains", 2, 2, [Arg.Text], ExprType.Bool,
            (ev, c, a) => Charged(ev, c, (string)a[0]!).Contains((string)a[1]!, StringComparison.Ordinal)));
        add(new("startsWith", 2, 2, [Arg.Text], ExprType.Bool,
            (ev, c, a) => Charged(ev, c, (string)a[0]!).StartsWith((string)a[1]!, StringComparison.Ordinal)));
    }

    public static string ToText(object value) => value switch
    {
        string s => s,
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        DateTime t => t.TimeOfDay == TimeSpan.Zero
            ? t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : t.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
        _ => string.Empty,
    };

    private static string Reserved(Evaluator ev, CallNode call, string text)
    {
        ev.Reserve(text.Length, call.Position);
        return text;
    }

    private static string Charged(Evaluator ev, CallNode call, string text)
    {
        ev.Charge(text.Length, call.Position);
        return text;
    }

    /// <summary>Joins the parts that are not null, empty or whitespace only, as the concat computed field does. Gives null when none is left.</summary>
    private static object? Concat(Evaluator ev, CallNode call, object?[] a)
    {
        var parts = new List<string>();
        long total = 0;
        foreach (var value in a)
        {
            if (value is null)
                continue;

            var text = ToText(value);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            parts.Add(text);
            total += text.Length;
            ev.Bound(total, call.Position);
        }

        ev.Charge((int)total, call.Position);
        return parts.Count == 0 ? null : string.Concat(parts);
    }

    private static object? Trim(Evaluator ev, CallNode call, object?[] a)
    {
        var text = Charged(ev, call, (string)a[0]!).Trim();
        ev.Bound(text.Length, call.Position);
        return text;
    }

    private static object? Take(Evaluator ev, CallNode call, object?[] a, bool fromEnd)
    {
        var text = (string)a[0]!;
        var count = (decimal)a[1]!;
        if (count < 0 || count != Math.Truncate(count))
            return ev.Local(Rules.ArgumentRange, call.Position, $"{call.Name} takes a whole number of characters, not below 0");

        var take = (int)Math.Min(count, text.Length);
        ev.Reserve(take, call.Position);
        return fromEnd ? text[^take..] : text[..take];
    }

    private static object? Replace(Evaluator ev, CallNode call, object?[] a)
    {
        var text = (string)a[0]!;
        var find = (string)a[1]!;
        var with = (string)a[2]!;
        if (find.Length == 0)
            return ev.Local(Rules.ArgumentRange, call.Position, "replace needs text to look for");

        ev.Charge(text.Length, call.Position);
        var hits = 0;
        for (var i = text.IndexOf(find, StringComparison.Ordinal); i >= 0; i = text.IndexOf(find, i + find.Length, StringComparison.Ordinal))
            hits++;

        ev.Bound(text.Length + ((long)hits * (with.Length - find.Length)), call.Position);
        return text.Replace(find, with, StringComparison.Ordinal);
    }
}
