using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;

namespace barakoCMS.Core.Validation;

/// <summary>
/// The rules a field's <see cref="FieldDefinition.ValidationRules"/> may carry, checked once when a
/// type is saved and applied to every entry write.
/// </summary>
/// <remarks>
/// A rule value arrives in more than one shape: a <see cref="JsonElement"/> from a raw request or a
/// stored document, and a <c>long</c>, <c>decimal</c> or <c>string</c> after
/// <c>ObjectJsonConverter</c> has read a body. Every reader here takes both.
///
/// A stored rule that would be refused today (an unknown name, a bound that is not a number) is
/// skipped on an entry write instead of failing it. The type was accepted before rules were
/// checked, and refusing every write to it would turn an ignored typo into an outage.
/// </remarks>
internal static class FieldRules
{
    public const string Min = "min";
    public const string Max = "max";
    public const string MinLength = "minLength";
    public const string MaxLength = "maxLength";
    public const string Pattern = "pattern";
    public const string RequiredWhen = "requiredWhen";

    /// <summary>The name <c>pattern</c> was first documented under, still accepted.</summary>
    public const string PatternAlias = "regex";

    public static readonly IReadOnlyList<string> Names =
        [Min, Max, MinLength, MaxLength, Pattern, RequiredWhen];

    public const int MaxPatternLength = 500;

    public const int MaxConditions = 20;

    /// <summary>How long one pattern match may run before the write is refused.</summary>
    public static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(250);

    private const RegexOptions PatternOptions = RegexOptions.CultureInvariant;

    public const string PatternTimedOut =
        "took too long to check against its pattern (rule 'pattern'), so it was refused.";

    private static readonly HashSet<string> TextTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "string", "text", "email", "url", "slug", "uuid", "richtext", "markdown", "time",
    };

    private static readonly HashSet<string> Operators = new(StringComparer.Ordinal)
    {
        "_eq", "_ne", "_in", "_nin", "_lt", "_lte", "_gt", "_gte",
    };

    // Answered here and not by ConditionEvaluator. That class also evaluates stored permission
    // rules, where an operator it does not know denies, so teaching it these four could turn a
    // stored rule that denies today into one that grants.
    private static readonly HashSet<string> Comparisons = new(StringComparer.Ordinal)
    {
        "_lt", "_lte", "_gt", "_gte",
    };

    private static readonly ConditionEvaluator Conditions = new();

    // The evaluator only reads the user for the $CURRENT_USER placeholder.
    private static readonly User NoUser = new();

    /// <summary>What is wrong with the rules a field declares, for the type validator.</summary>
    public static List<string> DefinitionErrors(FieldDefinition field)
    {
        var errors = new List<string>();
        var rules = field.ValidationRules;
        if (rules is null || rules.Count == 0)
            return errors;

        // One error for an oversized bag, so it cannot turn into an equally oversized response.
        if (rules.Count > Names.Count)
        {
            errors.Add($"Field '{field.Name}' declares {rules.Count} validation rules, and only "
                + $"{Names.Count} exist: {string.Join(", ", Names)}.");
            return errors;
        }

        var numeric = FieldTypeRegistry.IsNumericType(field.Type);
        var date = IsDateType(field.Type);
        var text = IsTextType(field.Type);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (name, raw) in rules)
        {
            var rule = Canonical(name);
            if (rule is null)
            {
                errors.Add($"Field '{field.Name}' has the unknown validation rule '{Shorten(name)}'. "
                    + $"Known rules: {string.Join(", ", Names)}. '{PatternAlias}' is read as '{Pattern}'.");
                continue;
            }

            if (!seen.Add(rule))
            {
                errors.Add(rule == Pattern
                    ? $"Field '{field.Name}' sets both '{Pattern}' and '{PatternAlias}', which are the same rule. Keep one."
                    : $"Field '{field.Name}' sets the rule '{rule}' more than once.");
                continue;
            }

            switch (rule)
            {
                case Min or Max:
                    if (numeric)
                    {
                        if (AsDecimal(raw) is null)
                            errors.Add($"Field '{field.Name}' is a number, so its rule '{rule}' must be a number.");
                    }
                    else if (date)
                    {
                        if (AsDate(raw) is null)
                            errors.Add($"Field '{field.Name}' is a date, so its rule '{rule}' must be a date.");
                    }
                    else
                    {
                        errors.Add($"Field '{field.Name}' has the rule '{rule}', which applies to number "
                            + $"and date fields, and is of type '{field.Type}'.");
                    }
                    break;

                case MinLength or MaxLength:
                    if (!text)
                        errors.Add($"Field '{field.Name}' has the rule '{rule}', which applies to text "
                            + $"fields, and is of type '{field.Type}'.");
                    else if (AsLength(raw) is null)
                        errors.Add($"Field '{field.Name}' has the rule '{rule}', which must be a whole "
                            + "number, zero or more.");
                    break;

                case Pattern:
                    if (!text)
                        errors.Add($"Field '{field.Name}' has the rule '{rule}', which applies to text "
                            + $"fields, and is of type '{field.Type}'.");
                    else if (PatternError(raw) is { } patternError)
                        errors.Add($"Field '{field.Name}' has a pattern that {patternError}.");
                    break;

                case RequiredWhen:
                    if (ConditionError(raw) is { } conditionError)
                        errors.Add($"Field '{field.Name}' has the rule '{rule}', which {conditionError}.");
                    break;
            }
        }

        if (errors.Count > 0)
            return errors;

        if (Bound(field, Min) is { } min && Bound(field, Max) is { } max && min.CompareTo(max) > 0)
            errors.Add($"Field '{field.Name}' has a 'min' above its 'max', so no value could pass.");

        if (Length(rules, MinLength) is { } minLength && Length(rules, MaxLength) is { } maxLength
            && minLength > maxLength)
            errors.Add($"Field '{field.Name}' has a 'minLength' above its 'maxLength', so no value could pass.");

        return errors;
    }

    /// <summary>What a present, correctly typed value breaks among the field's rules.</summary>
    public static List<string> ValueErrors(FieldDefinition field, object value)
    {
        var errors = new List<string>();
        var rules = field.ValidationRules;
        if (rules is null || rules.Count == 0)
            return errors;

        var label = $"Field '{field.DisplayName}' ({field.Name})";

        if (FieldTypeRegistry.IsNumericType(field.Type))
        {
            if (AsDecimal(value) is { } number)
            {
                if (TryGet(rules, Min, out var rawMin) && AsDecimal(rawMin) is { } min && number < min)
                    errors.Add($"{label} must be at least {Text(min)} (rule 'min').");
                if (TryGet(rules, Max, out var rawMax) && AsDecimal(rawMax) is { } max && number > max)
                    errors.Add($"{label} must be at most {Text(max)} (rule 'max').");
            }

            return errors;
        }

        if (IsDateType(field.Type))
        {
            if (AsDate(value) is { } moment)
            {
                if (TryGet(rules, Min, out var rawMin) && AsDate(rawMin) is { } min && moment < min)
                    errors.Add($"{label} must be on or after {Text(min)} (rule 'min').");
                if (TryGet(rules, Max, out var rawMax) && AsDate(rawMax) is { } max && moment > max)
                    errors.Add($"{label} must be on or before {Text(max)} (rule 'max').");
            }

            return errors;
        }

        if (!IsTextType(field.Type) || AsString(value) is not { } s)
            return errors;

        if (Length(rules, MinLength) is { } minLength && s.Length < minLength)
            errors.Add($"{label} must be at least {minLength} characters long (rule 'minLength').");

        if (Length(rules, MaxLength) is { } maxLength && s.Length > maxLength)
            errors.Add($"{label} must be at most {maxLength} characters long (rule 'maxLength').");

        if (TryGetPattern(rules, out var rawPattern) && AsString(rawPattern) is { Length: > 0 } pattern
            && pattern.Length <= MaxPatternLength)
        {
            try
            {
                if (!Regex.IsMatch(s, pattern, PatternOptions, PatternTimeout))
                    errors.Add($"{label} does not match the format this field requires (rule 'pattern').");
            }
            catch (RegexMatchTimeoutException)
            {
                errors.Add($"{label} {PatternTimedOut}");
            }
            catch (ArgumentException)
            {
                // A stored pattern that does not compile. Skipped, like any other stored rule a save
                // would refuse today.
            }
        }

        return errors;
    }

    /// <summary>Whether the field's <c>requiredWhen</c> condition holds for this data bag.</summary>
    public static bool IsRequiredBy(FieldDefinition field, Dictionary<string, object> data)
    {
        var rules = field.ValidationRules;
        if (rules is null || !TryGet(rules, RequiredWhen, out var raw))
            return false;

        if (ReadObject(raw) is not { Count: > 0 } condition)
            return false;

        // The validator matches data keys ignoring case and the evaluator matches them exactly, so
        // without this a caller could send "kind" for Kind and walk past the condition.
        var bag = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in data)
            bag[key] = value;

        foreach (var (name, comparison) in condition)
        {
            if (ReadObject(comparison) is not { Count: > 0 } operators)
                return false;

            var plain = new Dictionary<string, object>();
            foreach (var (op, bound) in operators)
            {
                if (!Comparisons.Contains(op))
                {
                    plain[op] = bound;
                    continue;
                }

                if (!bag.TryGetValue(name, out var actual) || !Compares(op, actual, bound))
                    return false;
            }

            if (plain.Count > 0
                && !Conditions.Evaluate(new Dictionary<string, object> { [name] = plain }, bag, NoUser))
                return false;
        }

        return true;
    }

    // Numbers as numbers, dates as dates. A value that is missing, or is neither, does not compare,
    // and the condition does not hold.
    private static bool Compares(string op, object? actual, object? bound)
    {
        int order;
        if (AsDecimal(actual) is { } number && AsDecimal(bound) is { } numberBound)
            order = number.CompareTo(numberBound);
        else if (AsDate(actual) is { } moment && AsDate(bound) is { } momentBound)
            order = moment.CompareTo(momentBound);
        else
            return false;

        return op switch
        {
            "_lt" => order < 0,
            "_lte" => order <= 0,
            "_gt" => order > 0,
            "_gte" => order >= 0,
            _ => false,
        };
    }

    /// <summary>Whether any field of the type stores a rule.</summary>
    public static bool HasRules(ContentTypeDefinition definition) =>
        definition.Fields is not null
        && definition.Fields.Any(f => f?.ValidationRules is { Count: > 0 });

    private static string? PatternError(object? raw)
    {
        if (AsString(raw) is not { Length: > 0 } pattern)
            return "must be a non-empty string";

        if (pattern.Length > MaxPatternLength)
            return $"is longer than {MaxPatternLength} characters";

        try
        {
            _ = new Regex(pattern, PatternOptions, PatternTimeout);
            return null;
        }
        catch (ArgumentException)
        {
            return "is not a valid regular expression";
        }
    }

    private static string? ConditionError(object? raw)
    {
        const string shape = "must be an object naming a field and a comparison, "
            + "for example { \"Kind\": { \"_eq\": \"Company\" } }";

        if (ReadObject(raw) is not { Count: > 0 } condition)
            return shape;

        if (condition.Count > MaxConditions)
            return $"names {condition.Count} fields, and at most {MaxConditions} are allowed";

        foreach (var (_, comparison) in condition)
        {
            if (ReadObject(comparison) is not { Count: > 0 } operators)
                return shape;

            if (operators.Keys.FirstOrDefault(o => !Operators.Contains(o)) is { } unknown)
                return $"uses the unknown comparison '{Shorten(unknown)}'. Known comparisons: "
                    + string.Join(", ", Operators.OrderBy(o => o, StringComparer.Ordinal));

            foreach (var (op, bound) in operators)
            {
                if (Comparisons.Contains(op) && AsDecimal(bound) is null && AsDate(bound) is null)
                    return $"compares with '{op}' against a value that is not a number or a date";
            }
        }

        return null;
    }

    private static IComparable? Bound(FieldDefinition field, string rule)
    {
        if (!TryGet(field.ValidationRules, rule, out var raw))
            return null;

        if (FieldTypeRegistry.IsNumericType(field.Type))
            return AsDecimal(raw);

        return IsDateType(field.Type) ? AsDate(raw) : null;
    }

    private static int? Length(Dictionary<string, object> rules, string rule) =>
        TryGet(rules, rule, out var raw) ? AsLength(raw) : null;

    private static string? Canonical(string? name)
    {
        if (name is null)
            return null;

        return name.Equals(PatternAlias, StringComparison.OrdinalIgnoreCase)
            ? Pattern
            : Names.FirstOrDefault(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    // A stored field carrying the rule under both names is one a save refuses, so neither applies.
    private static bool TryGetPattern(Dictionary<string, object> rules, out object? value)
    {
        var named = TryGet(rules, Pattern, out var byName);
        var aliased = TryGet(rules, PatternAlias, out var byAlias);

        value = named ? byName : byAlias;
        return named != aliased;
    }

    private static bool TryGet(Dictionary<string, object> rules, string rule, out object? value)
    {
        foreach (var (key, candidate) in rules)
        {
            if (key.Equals(rule, StringComparison.OrdinalIgnoreCase))
            {
                value = candidate;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static bool IsDateType(string? type) =>
        string.Equals(type, "date", StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, "datetime", StringComparison.OrdinalIgnoreCase);

    private static bool IsTextType(string? type) => type is not null && TextTypes.Contains(type);

    private static string? AsString(object? value) => value switch
    {
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
        _ => null,
    };

    private static decimal? AsDecimal(object? value)
    {
        switch (value)
        {
            case decimal m: return m;
            case int i: return i;
            case long l: return l;
            case short sh: return sh;
            case byte by: return by;
            case double d: return FromDouble(d);
            case float f: return FromDouble(f);
            case JsonElement { ValueKind: JsonValueKind.Number } je:
                return je.TryGetDecimal(out var fromJson) ? fromJson : null;
        }

        if (AsString(value) is not { } s)
            return null;

        return decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            || decimal.TryParse(s, out parsed)
            ? parsed
            : null;
    }

    // A cast from a double outside decimal's range throws.
    private static decimal? FromDouble(double d) =>
        double.IsFinite(d) && Math.Abs(d) < 7.9e28 ? (decimal)d : null;

    private static int? AsLength(object? value) =>
        AsDecimal(value) is { } m && m >= 0 && m <= int.MaxValue && m == decimal.Truncate(m)
            ? (int)m
            : null;

    private static DateTime? AsDate(object? value)
    {
        switch (value)
        {
            case DateTimeOffset o:
                return o.UtcDateTime;
            case DateTime d:
                return d.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(d, DateTimeKind.Utc)
                    : d.ToUniversalTime();
        }

        if (AsString(value) is not { } s)
            return null;

        return DateTime.TryParse(
            s,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    private static Dictionary<string, object>? ReadObject(object? value)
    {
        switch (value)
        {
            case Dictionary<string, object> dictionary:
                return dictionary;
            case IDictionary<string, object> other:
                return new Dictionary<string, object>(other);
            case JsonElement { ValueKind: JsonValueKind.Object } je:
                var read = new Dictionary<string, object>();
                foreach (var property in je.EnumerateObject())
                    read[property.Name] = property.Value.Clone();
                return read;
            default:
                return null;
        }
    }

    private static string Text(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Text(DateTime value) =>
        value.TimeOfDay == TimeSpan.Zero
            ? value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : value.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string Shorten(string value) => value.Length > 50 ? value[..50] : value;
}
