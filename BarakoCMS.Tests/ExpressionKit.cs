using System.Globalization;
using barakoCMS.Core.Expressions;
using Microsoft.Extensions.Time.Testing;

namespace BarakoCMS.Tests;

/// <summary>Shared setup for the expression tests: one schema, one fixed clock, and a way to print a value.</summary>
internal static class ExpressionKit
{
    public static readonly DateTimeOffset Start = new(2026, 11, 27, 10, 30, 0, TimeSpan.Zero);

    public static readonly Dictionary<string, ExprType> Schema = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Qty"] = ExprType.Number,
        ["Price"] = ExprType.Number,
        ["Name"] = ExprType.Text,
        ["Note"] = ExprType.Text,
        ["Born"] = ExprType.Date,
        ["Active"] = ExprType.Bool,
    };

    public static ExprType? Resolve(string field) => Schema.TryGetValue(field, out var type) ? type : null;

    public static ParseResult Check(string source, bool refuseClock = false, ExpressionLimits? limits = null) =>
        ExpressionEngine.ParseAndCheck(source, Resolve, refuseClock, limits);

    public static CheckedExpression Compile(string source, ExpressionLimits? limits = null)
    {
        var parsed = Check(source, limits: limits);
        if (parsed.Expression is null)
        {
            throw new InvalidOperationException($"'{source}' did not compile: {string.Join("; ", parsed.Errors)}");
        }

        return parsed.Expression;
    }

    public static EvalResult Run(string source, Dictionary<string, object?>? values = null, ExpressionLimits? limits = null, TimeProvider? clock = null)
    {
        var expression = Compile(source, limits);
        return ExpressionEngine.Evaluate(
            expression,
            name => values is not null && values.TryGetValue(name.Name, out var v) ? v : null,
            clock ?? new FakeTimeProvider(Start),
            limits);
    }

    /// <summary>A value as plain text, with text in single quotes so '' and null differ.</summary>
    public static string Show(object? value) => value switch
    {
        null => "null",
        string s => $"'{s}'",
        bool b => b ? "true" : "false",
        decimal d => d.ToString("0.############################", CultureInfo.InvariantCulture),
        DateTime t => t.TimeOfDay == TimeSpan.Zero
            ? t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        _ => value.GetType().Name,
    };

    public static string Shown(string source, Dictionary<string, object?>? values = null) => Show(Run(source, values).Value);
}
