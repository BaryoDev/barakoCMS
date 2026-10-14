using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using barakoCMS.Models;

namespace barakoCMS.Core.Validation;

/// <summary>
/// The JSON Schema a <c>json</c> field may declare in <see cref="FieldDefinition.JsonSchema"/>,
/// checked when the type is saved and applied to every entry write.
/// </summary>
/// <remarks>
/// A json field with no schema accepts any object or array, as it always has, and nothing here
/// applies to it.
///
/// A subset of JSON Schema 2020-12, the keywords listed in <see cref="Keywords"/>. A keyword outside
/// it is refused when the type is saved rather than ignored when an entry is written: a schema that
/// says <c>oneOf</c> and is read as saying nothing would pass the values it was written to refuse.
/// No <c>$ref</c>, so a schema is a finite tree, and its depth and size are capped, which bounds the
/// work one write can ask for. <c>format</c> is an annotation, as 2020-12 makes it by default.
///
/// The value an entry already holds is not checked again, so a schema added to a field with
/// entries does not refuse an edit of their other fields. Stored values that fail it keep reading.
/// </remarks>
internal static class JsonFieldSchemas
{
    public const int MaxDepth = 16;
    public const int MaxSchemaLength = 64 * 1024;
    private const int MaxErrors = 5;
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan PatternBudget = TimeSpan.FromMilliseconds(250);
    private const int MaxCachedPatterns = 1000;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Regex> Patterns = new(StringComparer.Ordinal);

    private static readonly string[] Types = ["object", "array", "string", "number", "integer", "boolean", "null"];

    /// <summary>The keywords a field's schema may use.</summary>
    public static IReadOnlyList<string> Keywords { get; } =
    [
        "type", "properties", "required", "additionalProperties", "items", "enum", "const",
        "minLength", "maxLength", "pattern", "minimum", "maximum", "minItems", "maxItems",
        "title", "description", "default", "examples", "format", "$comment", "$schema",
    ];

    public static bool Declares(FieldDefinition field) =>
        field.JsonSchema is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) };

    /// <summary>What is wrong with the schema a field declares, for the type validator.</summary>
    public static IEnumerable<string> DefinitionErrors(FieldDefinition field)
    {
        if (!Declares(field))
        {
            return [];
        }

        if (!string.Equals(field.Type, "json", StringComparison.OrdinalIgnoreCase))
        {
            return [$"Field '{field.Name}' declares jsonSchema but is of type '{field.Type}', not json."];
        }

        var schema = field.JsonSchema!.Value;
        if (schema.GetRawText().Length > MaxSchemaLength)
        {
            return [$"Field '{field.Name}' declares a jsonSchema longer than {MaxSchemaLength} characters."];
        }

        var errors = new List<string>();
        SchemaErrors(schema, "#", 1, errors);
        return errors.Take(MaxErrors).Select(e => $"Field '{field.Name}' jsonSchema {e}");
    }

    /// <summary>What is wrong with a schema in this subset, for a definition other than a field's.</summary>
    public static List<string> SchemaErrors(JsonElement schema)
    {
        var errors = new List<string>();
        if (schema.GetRawText().Length > MaxSchemaLength)
        {
            errors.Add($"is longer than {MaxSchemaLength} characters.");
        }
        else
        {
            SchemaErrors(schema, "#", 1, errors);
        }

        return errors.Take(MaxErrors).ToList();
    }

    /// <summary>Where a value fails a schema in this subset, each error naming the path from <paramref name="at"/>.</summary>
    public static List<string> Errors(JsonElement schema, JsonElement value, string at)
    {
        var errors = new Findings();
        Check(schema, value, at, errors);
        return errors.Take(MaxErrors).ToList();
    }

    private static void SchemaErrors(JsonElement schema, string at, int depth, List<string> errors)
    {
        if (schema.ValueKind == JsonValueKind.True || schema.ValueKind == JsonValueKind.False)
        {
            return;
        }

        if (schema.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"at {at} is not a schema: a schema is an object, true or false.");
            return;
        }
        if (depth > MaxDepth)
        {
            errors.Add($"nests deeper than {MaxDepth} levels at {at}.");
            return;
        }

        foreach (var keyword in schema.EnumerateObject())
        {
            var where = $"{at}/{keyword.Name}";
            if (!Keywords.Contains(keyword.Name, StringComparer.Ordinal))
            {
                errors.Add($"uses '{keyword.Name}' at {at}, which is not supported. Supported: {string.Join(", ", Keywords)}.");
            }
            else if (KeywordError(keyword.Name, keyword.Value) is { } error)
            {
                errors.Add($"at {where} {error}");
            }
            else if (keyword.Name == "properties")
            {
                foreach (var property in keyword.Value.EnumerateObject())
                {
                    SchemaErrors(property.Value, $"{where}/{property.Name}", depth + 1, errors);
                }
            }
            else if (keyword.Name is "items" or "additionalProperties")
            {
                SchemaErrors(keyword.Value, where, depth + 1, errors);
            }
        }
    }

    private static string? KeywordError(string name, JsonElement value) => name switch
    {
        "type" => value.ValueKind switch
        {
            JsonValueKind.String when Types.Contains(value.GetString()) => null,
            JsonValueKind.Array when value.GetArrayLength() > 0
                && value.EnumerateArray().All(t => t.ValueKind == JsonValueKind.String && Types.Contains(t.GetString())) => null,
            _ => $"names a type that is not one of {string.Join(", ", Types)}.",
        },
        "properties" => value.ValueKind == JsonValueKind.Object ? null : "must be an object of schemas.",
        "required" => value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(r => r.ValueKind == JsonValueKind.String)
            ? null : "must be a list of property names.",
        "enum" => value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0 ? null : "must be a list of at least one value.",
        "minLength" or "maxLength" or "minItems" or "maxItems" =>
            value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) && n >= 0 ? null : "must be a whole number, 0 or more.",
        "minimum" or "maximum" => value.ValueKind == JsonValueKind.Number ? null : "must be a number.",
        "pattern" => value.ValueKind == JsonValueKind.String && Compiles(value.GetString()!) ? null : "must be a regular expression.",
        _ => null,
    };

    private static bool Compiles(string pattern)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.None, PatternTimeout);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>What is wrong with a value written to a json field that declares a schema.</summary>
    /// <remarks>Each error names the path that failed, from the field's own name.</remarks>
    public static IEnumerable<string> ValueErrors(FieldDefinition field, object value, Content? existing)
    {
        if (!Declares(field) || !string.Equals(field.Type, "json", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        if (StoredValues.IsUnchanged(field.Name, value, existing))
        {
            return [];
        }

        var element = value is JsonElement je ? je : JsonSerializer.SerializeToElement(value);
        var errors = new Findings();
        Check(field.JsonSchema!.Value, element, field.Name, errors);

        return errors.Take(MaxErrors)
            .Select(e => $"Field '{field.DisplayName}' ({field.Name}) does not match its schema: {e}");
    }

    private static void Check(JsonElement schema, JsonElement value, string at, Findings errors)
    {
        if (errors.Count >= MaxErrors || schema.ValueKind == JsonValueKind.True)
        {
            return;
        }

        if (schema.ValueKind == JsonValueKind.False)
        {
            errors.Add($"{at} is not allowed.");
            return;
        }

        if (schema.TryGetProperty("type", out var type) && !TypeMatches(type, value))
        {
            errors.Add($"{at} should be {TypeText(type)} and is {KindOf(value)}.");
            return;
        }

        if (schema.TryGetProperty("const", out var constant) && !JsonElement.DeepEquals(constant, value))
        {
            errors.Add($"{at} should be {constant.GetRawText()}.");
        }

        if (schema.TryGetProperty("enum", out var options) && !options.EnumerateArray().Any(o => JsonElement.DeepEquals(o, value)))
        {
            errors.Add($"{at} should be one of {options.GetRawText()}.");
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Object: CheckObject(schema, value, at, errors); break;
            case JsonValueKind.Array: CheckArray(schema, value, at, errors); break;
            case JsonValueKind.String: CheckString(schema, value.GetString()!, at, errors); break;
            case JsonValueKind.Number: CheckNumber(schema, value, at, errors); break;
        }
    }

    private static void CheckObject(JsonElement schema, JsonElement value, string at, Findings errors)
    {
        if (schema.TryGetProperty("required", out var required))
        {
            foreach (var name in required.EnumerateArray().Select(r => r.GetString()!))
            {
                if (!value.TryGetProperty(name, out _))
                {
                    errors.Add($"{at}.{name} is required.");
                }
            }
        }

        schema.TryGetProperty("properties", out var properties);
        var hasExtra = schema.TryGetProperty("additionalProperties", out var additional);

        foreach (var property in value.EnumerateObject())
        {
            var where = $"{at}.{property.Name}";
            if (properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(property.Name, out var declared))
            {
                Check(declared, property.Value, where, errors);
            }
            else if (hasExtra)
            {
                Check(additional, property.Value, where, errors);
            }
        }
    }

    private static void CheckArray(JsonElement schema, JsonElement value, string at, Findings errors)
    {
        var count = value.GetArrayLength();
        if (schema.TryGetProperty("minItems", out var min) && count < min.GetInt32())
        {
            errors.Add($"{at} should hold at least {min.GetInt32()} items and holds {count}.");
        }

        if (schema.TryGetProperty("maxItems", out var max) && count > max.GetInt32())
        {
            errors.Add($"{at} should hold at most {max.GetInt32()} items and holds {count}.");
        }

        if (!schema.TryGetProperty("items", out var items))
        {
            return;
        }

        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            Check(items, item, $"{at}[{index++}]", errors);
        }
    }

    private static void CheckString(JsonElement schema, string value, string at, Findings errors)
    {
        var length = value.EnumerateRunes().Count();
        if (schema.TryGetProperty("minLength", out var min) && length < min.GetInt32())
        {
            errors.Add($"{at} should be at least {min.GetInt32()} characters.");
        }

        if (schema.TryGetProperty("maxLength", out var max) && length > max.GetInt32())
        {
            errors.Add($"{at} should be at most {max.GetInt32()} characters.");
        }

        if (!schema.TryGetProperty("pattern", out var pattern))
        {
            return;
        }

        if (errors.OutOfTime)
        {
            return;
        }

        var started = Stopwatch.GetTimestamp();
        bool matches;
        try
        {
            matches = Compiled(pattern.GetString()!).IsMatch(value);
        }
        catch (RegexMatchTimeoutException)
        {
            matches = false;
        }

        errors.PatternTime += Stopwatch.GetElapsedTime(started);
        if (errors.PatternTime > PatternBudget)
        {
            errors.OutOfTime = true;
            errors.Add($"{at} could not be checked against its patterns in time, so the value is refused.");
        }
        else if (!matches)
        {
            errors.Add($"{at} does not match the pattern {pattern.GetRawText()}.");
        }
    }

    /// <summary>
    /// The errors of one value's check, and the time its patterns have taken so far.
    /// </summary>
    /// <remarks>
    /// A timeout per match does not bound a write: a value of many strings can spend just under the
    /// timeout on each one. So every pattern a value is checked against shares one budget, and a
    /// value that runs past it is refused rather than let through unchecked.
    /// </remarks>
    private sealed class Findings : List<string>
    {
        public TimeSpan PatternTime { get; set; }
        public bool OutOfTime { get; set; }
    }

    private static Regex Compiled(string pattern)
    {
        if (Patterns.Count > MaxCachedPatterns)
        {
            Patterns.Clear();
        }

        return Patterns.GetOrAdd(pattern, p => new Regex(p, RegexOptions.CultureInvariant, PatternTimeout));
    }

    private static void CheckNumber(JsonElement schema, JsonElement value, string at, Findings errors)
    {
        var number = value.GetDouble();
        if (schema.TryGetProperty("minimum", out var min) && number < min.GetDouble())
        {
            errors.Add($"{at} should be at least {min.GetRawText()}.");
        }

        if (schema.TryGetProperty("maximum", out var max) && number > max.GetDouble())
        {
            errors.Add($"{at} should be at most {max.GetRawText()}.");
        }
    }

    private static bool TypeMatches(JsonElement type, JsonElement value) =>
        type.ValueKind == JsonValueKind.Array
            ? type.EnumerateArray().Any(t => Is(t.GetString()!, value))
            : Is(type.GetString()!, value);

    private static bool Is(string type, JsonElement value) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && IsWhole(value),
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => false,
    };

    private static bool IsWhole(JsonElement value) =>
        value.TryGetInt64(out _)
        || (value.TryGetDecimal(out var d) && decimal.Truncate(d) == d)
        || (double.TryParse(value.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            && double.IsFinite(x) && Math.Floor(x) == x);

    private static string TypeText(JsonElement type) =>
        type.ValueKind == JsonValueKind.Array
            ? "one of " + string.Join(", ", type.EnumerateArray().Select(t => t.GetString()))
            : $"{Article(type.GetString()!)} {type.GetString()}";

    private static string KindOf(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "an object",
        JsonValueKind.Array => "an array",
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        _ => "null",
    };

    private static string Article(string word) => "aeiou".Contains(word[0]) ? "an" : "a";
}
