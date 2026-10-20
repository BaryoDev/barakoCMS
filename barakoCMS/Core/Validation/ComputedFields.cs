using System.Globalization;
using System.Text.Json;
using barakoCMS.Models;

namespace barakoCMS.Core.Validation;

/// <summary>
/// Computed fields: a field whose value the server works out from other fields of the same entry.
/// </summary>
/// <remarks>
/// The function list is fixed, with no expression language and no code. The definition is checked
/// when the type is saved (inputs exist, their types fit the function, no field reads itself or
/// sits in a cycle). The value is worked out by <c>ContentWriter</c> inside every write, so a
/// route that forgets the rule still cannot skip it, and it is stored with the entry so it filters,
/// sorts and delivers like any field.
///
/// An input that is missing or blank leaves the result out of the entry, except for
/// <c>coalesce</c> and <c>concat</c>, which skip it. A division by zero leaves the result out too.
/// </remarks>
internal static class ComputedFields
{
    public const int MaxRound = 10;

    private static readonly string[] NumericTypes = ["int", "decimal", "money"];
    private static readonly string[] TextTypes = ["string", "text"];
    private static readonly string[] MomentTypes = ["date", "datetime"];

    /// <summary>The functions, with how many arguments each takes: min, and max (or -1 for no upper limit).</summary>
    private static readonly Dictionary<string, (int Min, int Max)> Functions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["add"] = (2, -1),
        ["subtract"] = (2, 2),
        ["multiply"] = (2, -1),
        ["divide"] = (2, 2),
        ["round"] = (1, 1),
        ["min"] = (2, -1),
        ["max"] = (2, -1),
        ["hours"] = (2, 2),
        ["days"] = (2, 2),
        ["duration"] = (2, 2),
        ["concat"] = (2, -1),
        ["coalesce"] = (2, -1),
    };

    public static IEnumerable<string> FunctionNames => Functions.Keys;

    public static bool IsComputed(FieldDefinition field) => field.Computed is not null;

    private static bool IsSpan(string fn) => fn.ToLowerInvariant() is "hours" or "days" or "duration";

    private static bool IsNumeric(string fn) =>
        fn.ToLowerInvariant() is "add" or "subtract" or "multiply" or "divide" or "round" or "min" or "max" or "hours" or "days" or "duration";

    /// <summary>What is wrong with the computed fields of a type, for the type validator.</summary>
    public static List<string> DefinitionErrors(IReadOnlyCollection<FieldDefinition> fields)
    {
        var errors = new List<string>();
        var computed = fields.Where(f => f is not null && IsComputed(f) && !string.IsNullOrWhiteSpace(f.Name)).ToList();

        foreach (var field in computed)
            errors.AddRange(FieldErrors(field, fields));

        if (errors.Count == 0)
            errors.AddRange(CycleErrors(computed));

        return errors;
    }

    private static IEnumerable<string> FieldErrors(FieldDefinition field, IReadOnlyCollection<FieldDefinition> fields)
    {
        var def = field.Computed!;
        var name = field.Name;

        if (!Functions.TryGetValue(def.Fn ?? string.Empty, out var arity))
        {
            yield return $"Field '{name}' computes with '{def.Fn}', which is not a function. Use {string.Join(", ", Functions.Keys)}.";
            yield break;
        }

        var fn = def.Fn!.ToLowerInvariant();
        var args = def.Args ?? [];

        if (args.Count < arity.Min || (arity.Max >= 0 && args.Count > arity.Max))
        {
            var takes = arity.Max == arity.Min ? $"{arity.Min}" : arity.Max < 0 ? $"at least {arity.Min}" : $"{arity.Min} to {arity.Max}";
            yield return $"Field '{name}' computes with {fn}, which takes {takes} arguments, and {args.Count} were given.";
            yield break;
        }

        if (def.Round is { } digits && (digits < 0 || digits > MaxRound))
            yield return $"Field '{name}' rounds to {digits} places, and rounding is 0 to {MaxRound}.";

        if (def.Round is not null && !IsNumeric(fn))
            yield return $"Field '{name}' computes with {fn}, which does not give a number, so it takes no round.";

        if (field.IsRequired)
            yield return $"Field '{name}' is computed by the server, so it cannot be required of a caller.";

        if (field.DefaultValue is not null)
            yield return $"Field '{name}' is computed by the server, so it takes no default value.";

        var resultType = field.Type.ToLowerInvariant();
        if (IsNumeric(fn) && !NumericTypes.Contains(resultType))
            yield return $"Field '{name}' computes with {fn}, which gives a number, so its type has to be int, decimal or money, not {field.Type}.";
        if (fn == "concat" && !TextTypes.Contains(resultType))
            yield return $"Field '{name}' computes with concat, which gives text, so its type has to be string or text, not {field.Type}.";

        var inputs = new List<FieldDefinition>();
        foreach (var arg in args)
        {
            if (IsNumeric(fn) && !IsSpan(fn) && TryLiteral(arg, out _))
                continue;

            var input = fields.FirstOrDefault(f => f is not null && Matches(f.Name, arg));
            if (input is null)
            {
                yield return $"Field '{name}' computes from '{arg}', which the type does not declare.";
                continue;
            }

            inputs.Add(input);

            if (Matches(input.Name, name))
            {
                yield return $"Field '{name}' computes from itself.";
                continue;
            }

            if (SensitivityRank.Exposure(field.Sensitivity) > SensitivityRank.Exposure(input.Sensitivity))
            {
                yield return $"Field '{name}' is {field.Sensitivity} and computes from '{input.Name}', which is {input.Sensitivity}. "
                    + "A computed value may not be more widely readable than what it is computed from.";
            }
        }

        foreach (var error in InputTypeErrors(field, fn, inputs))
            yield return error;
    }

    private static IEnumerable<string> InputTypeErrors(FieldDefinition field, string fn, List<FieldDefinition> inputs)
    {
        var name = field.Name;

        if (IsSpan(fn))
        {
            var kinds = inputs.Select(i => KindOf(i.Type)).ToList();
            if (kinds.Count == 2 && (kinds.Contains(null) || kinds[0] != kinds[1]))
            {
                yield return $"Field '{name}' computes with {fn}, which takes two dates, two datetimes or two times of day, "
                    + $"and got {string.Join(" and ", inputs.Select(i => $"'{i.Name}' ({i.Type})"))}.";
            }

            yield break;
        }

        if (IsNumeric(fn))
        {
            foreach (var input in inputs.Where(i => !NumericTypes.Contains(i.Type.ToLowerInvariant())))
                yield return $"Field '{name}' computes with {fn}, which takes numbers, and '{input.Name}' is {input.Type}.";

            yield break;
        }

        if (fn == "coalesce")
        {
            foreach (var input in inputs.Where(i => !string.Equals(i.Type, field.Type, StringComparison.OrdinalIgnoreCase)))
                yield return $"Field '{name}' computes with coalesce, which takes fields of the type of its result, {field.Type}, and '{input.Name}' is {input.Type}.";

            yield break;
        }

        foreach (var input in inputs.Where(i => !IsScalarText(i.Type)))
            yield return $"Field '{name}' computes with concat, which takes single values, and '{input.Name}' is {input.Type}.";
    }

    private static bool IsScalarText(string type) =>
        type.ToLowerInvariant() is not ("array" or "object" or "json" or "geopoint" or "file" or "inlineimage" or "token");

    private static IEnumerable<string> CycleErrors(List<FieldDefinition> computed)
    {
        var edges = computed.ToDictionary(
            f => f.Name,
            f => (f.Computed!.Args ?? []).Where(a => computed.Any(c => Matches(c.Name, a))).ToList(),
            StringComparer.OrdinalIgnoreCase);

        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var start in edges.Keys)
        {
            var path = new List<string>();
            if (Visit(start, edges, state, path))
            {
                yield return $"Computed fields read each other in a circle: {string.Join(", ", path)}.";
                yield break;
            }
        }
    }

    private static bool Visit(string node, Dictionary<string, List<string>> edges, Dictionary<string, int> state, List<string> path)
    {
        if (state.GetValueOrDefault(node) == 2)
            return false;

        if (state.GetValueOrDefault(node) == 1)
        {
            path.Add(node);
            return true;
        }

        state[node] = 1;
        path.Add(node);
        foreach (var next in edges.GetValueOrDefault(node) ?? [])
        {
            var key = edges.Keys.First(k => Matches(k, next));
            if (Visit(key, edges, state, path))
                return true;
        }

        path.RemoveAt(path.Count - 1);
        state[node] = 2;
        return false;
    }

    /// <summary>The computed fields in the order they can be worked out: a field after the computed fields it reads.</summary>
    /// <remarks>Assumes the type passed <see cref="DefinitionErrors"/>. A cycle that got through is cut rather than looped on.</remarks>
    public static List<FieldDefinition> InOrder(IEnumerable<FieldDefinition> fields)
    {
        var computed = fields.Where(f => f is not null && IsComputed(f) && !string.IsNullOrWhiteSpace(f.Name)).ToList();
        var ordered = new List<FieldDefinition>();
        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (ordered.Count < computed.Count)
        {
            var next = computed.Where(f => !placed.Contains(f.Name))
                .FirstOrDefault(f => (f.Computed!.Args ?? []).All(a =>
                    placed.Contains(a) || !computed.Any(c => Matches(c.Name, a))));

            next ??= computed.First(f => !placed.Contains(f.Name));
            ordered.Add(next);
            placed.Add(next.Name);
        }

        return ordered;
    }

    /// <summary>
    /// Removes whatever the data holds under each computed field and puts the computed value in its
    /// place, or nothing when the inputs do not give one.
    /// </summary>
    public static void Apply(IEnumerable<FieldDefinition> fields, Dictionary<string, object> data)
    {
        foreach (var field in InOrder(fields))
        {
            foreach (var key in data.Keys.Where(k => Matches(k, field.Name)).ToList())
                data.Remove(key);

            if (Compute(field, data) is { } value)
                data[field.Name] = value;
        }
    }

    /// <summary>The value of one computed field over the data, or null when the inputs give none.</summary>
    public static object? Compute(FieldDefinition field, IReadOnlyDictionary<string, object> data)
    {
        var def = field.Computed!;
        var fn = def.Fn.ToLowerInvariant();
        var args = def.Args ?? [];

        if (fn == "concat")
        {
            var parts = args.Select(a => TextOf(Find(data, a))).Where(t => !string.IsNullOrEmpty(t)).ToList();
            return parts.Count == 0 ? null : string.Concat(parts);
        }

        if (fn == "coalesce")
        {
            foreach (var arg in args)
            {
                var value = Find(data, arg);
                if (!IsBlank(value))
                    return Unwrap(value!);
            }

            return null;
        }

        if (IsSpan(fn))
        {
            if (!TryMoment(Find(data, args[0]), out var from)
                || !TryMoment(Find(data, args[1]), out var to))
            {
                return null;
            }

            var span = to - from;
            var amount = fn switch
            {
                "hours" => (decimal)span.TotalHours,
                "days" => (decimal)span.TotalDays,
                _ => (decimal)span.TotalMinutes,
            };
            return Shape(field, amount);
        }

        return ComputeNumber(field, fn, args, data);
    }

    private static object? ComputeNumber(FieldDefinition field, string fn, List<string> args, IReadOnlyDictionary<string, object> data)
    {
        var numbers = new List<decimal>();
        foreach (var arg in args)
        {
            if (TryLiteral(arg, out var literal))
            {
                numbers.Add(literal);
                continue;
            }

            if (NumberOf(Find(data, arg)) is not { } number)
                return null;

            numbers.Add(number);
        }

        decimal result;
        try
        {
            result = fn switch
            {
                "add" => numbers.Aggregate(0m, (a, b) => a + b),
                "subtract" => numbers[0] - numbers[1],
                "multiply" => numbers.Aggregate(1m, (a, b) => a * b),
                "divide" => numbers[1] == 0 ? throw new DivideByZeroException() : numbers[0] / numbers[1],
                "min" => numbers.Min(),
                "max" => numbers.Max(),
                _ => numbers[0],
            };
        }
        catch (Exception e) when (e is DivideByZeroException or OverflowException)
        {
            return null;
        }

        return Shape(field, result);
    }

    /// <summary>A number rounded as the field asks, and as a whole number for an int field.</summary>
    private static object Shape(FieldDefinition field, decimal value)
    {
        var digits = string.Equals(field.Type, "int", StringComparison.OrdinalIgnoreCase)
            ? 0
            : field.Computed!.Round ?? (field.Computed.Fn.Equals("round", StringComparison.OrdinalIgnoreCase) ? 0 : (int?)null);

        if (digits is { } places)
            value = Math.Round(value, places, MidpointRounding.AwayFromZero);

        return string.Equals(field.Type, "int", StringComparison.OrdinalIgnoreCase) ? (object)(long)value : value;
    }

    /// <summary>Whether a value sent for a computed field is the one already stored, which a client echoing an entry back sends.</summary>
    public static bool SameAsStored(object? sent, object? stored) =>
        (IsBlank(sent) && IsBlank(stored))
        || (NumberOf(sent) is { } a && NumberOf(stored) is { } b && a == b)
        || (!IsBlank(sent) && !IsBlank(stored) && string.Equals(TextOf(sent), TextOf(stored), StringComparison.Ordinal));

    public static string RefusedMessage(FieldDefinition field) =>
        $"Field '{field.DisplayName}' ({field.Name}) is computed by the server and cannot be set. Leave it out.";

    private static object? Find(IReadOnlyDictionary<string, object> data, string name)
    {
        foreach (var (key, value) in data)
        {
            if (Matches(key, name))
                return value;
        }

        return null;
    }

    private static bool Matches(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool TryLiteral(string arg, out decimal value) =>
        decimal.TryParse(arg, NumberStyles.Number, CultureInfo.InvariantCulture, out value);

    private static string? KindOf(string type) => type.ToLowerInvariant() switch
    {
        "date" or "datetime" => "moment",
        "time" => "time",
        _ => null,
    };

    private static bool IsBlank(object? value) => value switch
    {
        null => true,
        string s => string.IsNullOrWhiteSpace(s),
        JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => true,
        JsonElement { ValueKind: JsonValueKind.String } je => string.IsNullOrWhiteSpace(je.GetString()),
        _ => false,
    };

    private static object Unwrap(object value) => value is JsonElement je
        ? je.ValueKind switch
        {
            JsonValueKind.String => je.GetString()!,
            JsonValueKind.Number => je.TryGetInt64(out var l) ? l : je.GetDecimal(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => je.Clone(),
        }
        : value;

    private static string? TextOf(object? value)
    {
        if (IsBlank(value))
            return null;

        return Unwrap(value!) switch
        {
            string s => s,
            bool b => b ? "true" : "false",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            var other => other.ToString(),
        };
    }

    private static decimal? NumberOf(object? value)
    {
        if (IsBlank(value))
            return null;

        return Unwrap(value!) switch
        {
            long l => l,
            int i => i,
            decimal d => d,
            double d when !double.IsNaN(d) && !double.IsInfinity(d) => (decimal)d,
            string s when decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    /// <summary>A date, a datetime or a time of day as a point on one line, so two of them subtract.</summary>
    private static bool TryMoment(object? value, out TimeSpan point)
    {
        point = default;
        var text = TextOf(value);
        if (text is null)
            return false;

        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var moment)
            && text.Contains('-'))
        {
            point = moment.UtcDateTime - DateTime.UnixEpoch;
            return true;
        }

        return TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out point);
    }
}
