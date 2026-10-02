using System.Collections.Concurrent;
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
/// A stored rule that would be refused today (an unknown name, a bound that is not a number, a min
/// above its max) is skipped on an entry write instead of failing it. The type was accepted before
/// rules were checked and no endpoint edits a stored rule, so refusing every write to it would turn
/// an ignored typo into an outage. <see cref="Classify"/> and the write path read a rule through
/// the same helpers, so what the startup notice calls applied is what a write applies.
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

    // Matched without backtracking, so the time a match takes grows with the length of the value
    // and not with the shape of the pattern. One write can check many patterns and an import checks
    // thousands of entries, so a per-match timeout alone would still add up to minutes of a core.
    private const RegexOptions PatternOptions = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;

    public const string PatternTimedOut =
        "took too long to check against its pattern (rule 'pattern'), so it was refused.";

    private const int MaxCachedPatterns = 1000;

    // The built-in cache holds fifteen, and a type with more patterns than that would rebuild one
    // on every write. Null is a pattern that cannot be built.
    private static readonly ConcurrentDictionary<string, Regex?> Patterns = new(StringComparer.Ordinal);

    private const string CurrentUser = "$CURRENT_USER";

    private const double DecimalRange = 7.9e28;

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

    // Never read: a condition naming $CURRENT_USER is refused on save and skipped on a write.
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

        if (Contradicts(RawBound(field, Min), RawBound(field, Max)))
            errors.Add($"Field '{field.Name}' has a 'min' above its 'max', so no value could pass.");

        if (Contradicts(RawLength(rules, MinLength), RawLength(rules, MaxLength)))
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
            var (min, max) = NumberBounds(rules);

            if (AsDecimal(value) is { } number)
            {
                if (min is { } least && number < least)
                    errors.Add($"{label} must be at least {Text(least)} (rule 'min').");
                if (max is { } most && number > most)
                    errors.Add($"{label} must be at most {Text(most)} (rule 'max').");
            }
            else if (BeyondDecimal(value) is { } sign)
            {
                // The request converter hands over a double for a number past decimal's range. It
                // cannot be compared as a decimal, and its sign says which bound it is past.
                if (sign < 0 && min is { } low)
                    errors.Add($"{label} must be at least {Text(low)} (rule 'min').");
                if (sign > 0 && max is { } high)
                    errors.Add($"{label} must be at most {Text(high)} (rule 'max').");
            }

            return errors;
        }

        if (IsDateType(field.Type))
        {
            var (earliest, latest) = DateBounds(rules);

            if (AsDate(value) is { } moment)
            {
                if (earliest is { } start && moment < start)
                    errors.Add($"{label} must be on or after {Text(start)} (rule 'min').");
                if (latest is { } end && moment > end)
                    errors.Add($"{label} must be on or before {Text(end)} (rule 'max').");
            }

            return errors;
        }

        if (!IsTextType(field.Type) || AsString(value) is not { } s)
            return errors;

        // A required field left blank never gets here, the required check refuses it first. So this
        // is an optional field that was cleared, which a console sends as an empty string, and
        // clearing an optional field has to stay possible whatever its rules say about a value.
        if (string.IsNullOrWhiteSpace(s))
            return errors;

        var (shortest, longest) = LengthBounds(rules);

        if (shortest is { } fewest && s.Length < fewest)
            errors.Add($"{label} must be at least {fewest} characters long (rule 'minLength').");

        if (longest is { } widest && s.Length > widest)
            errors.Add($"{label} must be at most {widest} characters long (rule 'maxLength').");

        if (UsablePattern(rules) is not { } pattern)
            return errors;

        // In .NET "$" also matches before a final line break, and a check of the same pattern in a
        // browser does not, so "123\n" would pass here and fail there.
        if (s.EndsWith('\n'))
        {
            errors.Add($"{label} must not end with a line break (rule 'pattern').");
            return errors;
        }

        try
        {
            if (!pattern.IsMatch(s))
                errors.Add($"{label} does not match the format this field requires (rule 'pattern').");
        }
        catch (RegexMatchTimeoutException)
        {
            errors.Add($"{label} {PatternTimedOut}");
        }

        return errors;
    }

    /// <summary>Whether the field's <c>requiredWhen</c> condition holds for this data bag.</summary>
    public static bool IsRequiredBy(FieldDefinition field, Dictionary<string, object> data)
    {
        var rules = field.ValidationRules;
        if (rules is null || !TryGet(rules, RequiredWhen, out var raw))
            return false;

        if (ConditionError(raw) is not null || ReadObject(raw) is not { Count: > 0 } condition)
            return false;

        // The validator matches data keys ignoring case and the evaluator matches them exactly, so
        // without this a caller could send "kind" for Kind and walk past the condition. First key
        // wins, as it does where the validator reads a field's value, so a bag holding both Kind
        // and kind is read the same way in both places.
        var bag = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in data)
            bag.TryAdd(key, value);

        foreach (var (name, comparison) in condition)
        {
            if (ReadObject(comparison) is not { Count: > 0 } operators)
                return false;

            var present = bag.ContainsKey(name);

            var plain = new Dictionary<string, object>();
            foreach (var (op, bound) in operators)
            {
                if (!Comparisons.Contains(op))
                {
                    plain[op] = bound;
                    continue;
                }

                if (!present || !Compares(op, bag[name], bound))
                    return false;
            }

            if (plain.Count == 0)
                continue;

            // A field that was left out is not equal to anything and is not in any list. For "not
            // equal" and "not in" it is read as null, the same as a field sent as null: otherwise
            // leaving the controlling field out would walk past the rule.
            if (!present && plain.Keys.Any(o => o is "_eq" or "_in"))
                return false;

            var subject = present
                ? bag
                : new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { [name] = null! };

            if (!Conditions.Evaluate(new Dictionary<string, object> { [name] = plain }, subject, NoUser))
                return false;
        }

        return true;
    }

    /// <summary>
    /// The rule names a field stores, split into the ones an entry write applies and the ones it
    /// skips.
    /// </summary>
    public static (List<string> Applied, List<string> Skipped) Classify(FieldDefinition field)
    {
        var applied = new List<string>();
        var skipped = new List<string>();

        var rules = field.ValidationRules;
        if (rules is null)
            return (applied, skipped);

        var numeric = FieldTypeRegistry.IsNumericType(field.Type);
        var date = IsDateType(field.Type);
        var text = IsTextType(field.Type);

        foreach (var (name, raw) in rules)
        {
            var works = Canonical(name) switch
            {
                Min => numeric ? NumberBounds(rules).Min is not null : date && DateBounds(rules).Min is not null,
                Max => numeric ? NumberBounds(rules).Max is not null : date && DateBounds(rules).Max is not null,
                MinLength => text && LengthBounds(rules).Min is not null,
                MaxLength => text && LengthBounds(rules).Max is not null,
                Pattern => text && UsablePattern(rules) is not null,
                RequiredWhen => ConditionError(raw) is null,
                _ => false,
            };

            (works ? applied : skipped).Add(name);
        }

        return (applied, skipped);
    }

    /// <summary>Whether any field of the type stores a rule.</summary>
    public static bool HasRules(ContentTypeDefinition definition) =>
        definition.Fields is not null
        && definition.Fields.Any(f => f?.ValidationRules is { Count: > 0 });

    // Numbers as numbers, dates as dates. A value that is neither does not compare, and the
    // condition does not hold.
    private static bool Compares(string op, object? actual, object? bound)
    {
        int order;
        if (AsDecimal(bound) is { } numberBound)
        {
            if (AsDecimal(actual) is { } number)
                order = number.CompareTo(numberBound);
            else if (BeyondDecimal(actual) is { } sign)
                order = sign;
            else
                return false;
        }
        else if (AsDate(actual) is { } moment && AsDate(bound) is { } momentBound)
        {
            order = moment.CompareTo(momentBound);
        }
        else
        {
            return false;
        }

        return op switch
        {
            "_lt" => order < 0,
            "_lte" => order <= 0,
            "_gt" => order > 0,
            "_gte" => order >= 0,
            _ => false,
        };
    }

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
        catch (NotSupportedException)
        {
            return "uses a lookahead, a lookbehind, a backreference or an atomic group, or is too "
                + "large to build. Patterns are matched without backtracking, which supports none of those";
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

        foreach (var (name, comparison) in condition)
        {
            // Permission conditions read document properties this way. An entry being validated
            // has none yet, so the condition could never hold.
            if (name.StartsWith('$'))
                return $"names '{Shorten(name)}', and a condition here reads the entry's own fields, "
                    + "not a property of the document";

            if (ReadObject(comparison) is not { Count: > 0 } operators)
                return shape;

            if (operators.Keys.FirstOrDefault(o => !Operators.Contains(o)) is { } unknown)
                return $"uses the unknown comparison '{Shorten(unknown)}'. Known comparisons: "
                    + string.Join(", ", Operators.OrderBy(o => o, StringComparer.Ordinal));

            foreach (var (op, bound) in operators)
            {
                if (Mentions(bound, CurrentUser))
                    return $"compares against {CurrentUser}, which only a permission condition can fill in";

                if (Comparisons.Contains(op) && AsDecimal(bound) is null && AsDate(bound) is null)
                    return $"compares with '{op}' against a value that is not a number or a date";
            }
        }

        return null;
    }

    private static bool Mentions(object? bound, string text) => bound switch
    {
        null => false,
        string s => s == text,
        JsonElement { ValueKind: JsonValueKind.String } je => je.GetString() == text,
        JsonElement { ValueKind: JsonValueKind.Array } je => je.EnumerateArray()
            .Any(item => item.ValueKind == JsonValueKind.String && item.GetString() == text),
        System.Collections.IEnumerable list => list.Cast<object?>().Any(item => Mentions(item, text)),
        _ => false,
    };

    // The pairs below return nothing for a pair that contradicts itself. No value could pass both,
    // so applying a stored one would refuse every write of the field.

    private static (decimal? Min, decimal? Max) NumberBounds(Dictionary<string, object> rules)
    {
        decimal? min = TryGet(rules, Min, out var rawMin) ? AsDecimal(rawMin) : null;
        decimal? max = TryGet(rules, Max, out var rawMax) ? AsDecimal(rawMax) : null;

        if (min > max)
            return (null, null);

        return (min, max);
    }

    private static (DateTime? Min, DateTime? Max) DateBounds(Dictionary<string, object> rules)
    {
        DateTime? min = TryGet(rules, Min, out var rawMin) ? AsDate(rawMin) : null;
        DateTime? max = TryGet(rules, Max, out var rawMax) ? AsDate(rawMax) : null;

        if (min > max)
            return (null, null);

        return (min, max);
    }

    private static (int? Min, int? Max) LengthBounds(Dictionary<string, object> rules)
    {
        var min = RawLength(rules, MinLength);
        var max = RawLength(rules, MaxLength);

        if (min > max)
            return (null, null);

        return (min, max);
    }

    private static Regex? UsablePattern(Dictionary<string, object> rules)
    {
        if (!TryGetPattern(rules, out var raw) || AsString(raw) is not { Length: > 0 } pattern)
            return null;

        return pattern.Length > MaxPatternLength ? null : Compiled(pattern);
    }

    private static Regex? Compiled(string pattern)
    {
        if (Patterns.TryGetValue(pattern, out var cached))
            return cached;

        Regex? built;
        try
        {
            built = new Regex(pattern, PatternOptions, PatternTimeout);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            built = null;
        }

        if (Patterns.Count >= MaxCachedPatterns)
            Patterns.Clear();

        Patterns[pattern] = built;
        return built;
    }

    private static bool Contradicts(IComparable? min, IComparable? max) =>
        min is not null && max is not null && min.CompareTo(max) > 0;

    private static IComparable? RawBound(FieldDefinition field, string rule)
    {
        if (!TryGet(field.ValidationRules, rule, out var raw))
            return null;

        if (FieldTypeRegistry.IsNumericType(field.Type))
            return AsDecimal(raw);

        return IsDateType(field.Type) ? AsDate(raw) : null;
    }

    private static int? RawLength(Dictionary<string, object> rules, string rule) =>
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
                if (je.TryGetDecimal(out var fromJson))
                    return fromJson;
                return je.TryGetDouble(out var wide) ? FromDouble(wide) : null;
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
        double.IsFinite(d) && Math.Abs(d) < DecimalRange ? (decimal)d : null;

    /// <summary>
    /// The sign of a finite number too large for a decimal, or null for anything else.
    /// </summary>
    private static int? BeyondDecimal(object? value)
    {
        double? read = value switch
        {
            double d => d,
            float f => f,
            JsonElement { ValueKind: JsonValueKind.Number } je when je.TryGetDouble(out var wide) => wide,
            _ => null,
        };

        return read is { } number && double.IsFinite(number) && Math.Abs(number) >= DecimalRange
            ? Math.Sign(number)
            : null;
    }

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
