using System.Text.Json;
using System.Text.RegularExpressions;

namespace barakoCMS.Core.Validation;

/// <summary>
/// The single source of truth for content-type field types.
///
/// Two layers of validation used to live in three places that drifted apart
/// (the two <c>*ValidatorService</c>s and this namespace's static helpers): one
/// accepted <c>text</c>/<c>number</c>, another rejected them, and a doc comment
/// advertised <c>richtext</c>/<c>reference</c> that no validator accepted. Every
/// validator now reads from this registry, so the allowed set and the per-type
/// value check can never disagree. Adding a field type is one entry here.
///
/// Because all content shares one <c>Content</c> document with a JSONB
/// <c>Data</c> bag, most new types are "a string plus a format rule", validated
/// at the application layer, Contentful/Sanity style.
///
/// That includes <c>reference</c>. Real relational integrity, typed columns and
/// foreign keys, was considered and refused for 4.0: it would mean a migration
/// for every field added, and the content model here is defined at runtime by
/// whoever is clicking around the admin. The two cannot both be true.
/// </summary>
public static class FieldTypeRegistry
{
    /// <summary>What a field type is, for validation and for the admin editor.</summary>
    /// <param name="Name">Canonical lower-case name.</param>
    /// <param name="EditorHint">
    /// Hint the admin uses to pick an input control (e.g. <c>text</c>, <c>email</c>,
    /// <c>richtext</c>, <c>money</c>, <c>datetime</c>). Falls back to <c>text</c>.
    /// </param>
    /// <param name="IsValidValue">Does a supplied value conform to this type?</param>
    public sealed record FieldTypeSpec(string Name, string EditorHint, Func<object, bool> IsValidValue);

    // Compiled once. Email/slug are intentionally pragmatic, not RFC-exhaustive:
    // enough to catch obvious mistakes without rejecting legitimate values.
    private static readonly Regex EmailRegex =
        new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);
    private static readonly Regex SlugRegex =
        new(@"^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.Compiled);

    // Canonical spec per distinct type. Aliases are wired up in the lookup below so
    // both a spec's canonical name and its aliases resolve to the same behaviour.
    private static readonly FieldTypeSpec[] Specs =
    {
        // --- Existing primitives (behaviour preserved) ------------------------
        new("string",   "text",     IsString),
        new("text",     "textarea", IsString),
        new("int",      "number",   IsInteger),
        new("bool",     "checkbox", IsBoolean),
        new("datetime", "datetime", IsDateTime),
        new("date",     "date",     IsDateTime),
        new("decimal",  "number",   IsDecimal),
        new("array",    "tags",     IsArray),
        new("object",   "json",     IsObject),

        // --- F.1 validation-shaped types --------------------------------------
        // Mostly a string plus a format check, so they read as their own type in
        // the admin and reject malformed values at the API instead of silently
        // storing junk in the JSON bag.
        new("email",    "email",    v => AsString(v) is { } s && EmailRegex.IsMatch(s)),
        new("url",      "url",      v => AsString(v) is { } s && IsAbsoluteUrl(s)),
        new("slug",     "slug",     v => AsString(v) is { } s && SlugRegex.IsMatch(s)),
        new("uuid",     "text",     v => AsString(v) is { } s && Guid.TryParse(s, out _)),
        new("richtext", "richtext", IsString),
        new("markdown", "markdown", IsString),
        new("time",     "time",     v => AsString(v) is { } s && IsTime(s)),
        new("json",     "json",     IsJson),
        new("money",    "money",    IsDecimal),

        // A pointer to another content item, Contentful and Sanity style rather than a real foreign
        // key. Real relational integrity would mean typed columns and a migration for every new
        // field, which cannot work when the content model is defined at runtime through the admin.
        //
        // Only the shape is checked here, because this registry is a pure function of the value and
        // cannot reach the database. That the target exists, and is of the declared type, is checked
        // by ContentValidatorService, which has a session.
        new("reference", "reference", v => AsString(v) is { } s && Guid.TryParse(s, out _)),
    };

    // Alias -> canonical spec. Aliases are the historical synonyms both live
    // validators already accepted; keeping them here means no existing content
    // type breaks and there is still exactly one place that defines the set.
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["integer"] = "int",
        ["number"]  = "int",
        ["boolean"] = "bool",
    };

    private static readonly Dictionary<string, FieldTypeSpec> Lookup = BuildLookup();

    private static Dictionary<string, FieldTypeSpec> BuildLookup()
    {
        var map = new Dictionary<string, FieldTypeSpec>(StringComparer.OrdinalIgnoreCase);
        foreach (var spec in Specs)
            map[spec.Name] = spec;
        foreach (var (alias, canonical) in Aliases)
            map[alias] = map[canonical];
        return map;
    }

    /// <summary>Every accepted type name, aliases included, sorted for stable error messages.</summary>
    public static IReadOnlyList<string> AllowedTypeNames { get; } =
        Lookup.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();

    /// <summary>Is <paramref name="type"/> a known field type (case-insensitive)?</summary>
    public static bool IsKnownType(string? type) =>
        !string.IsNullOrWhiteSpace(type) && Lookup.ContainsKey(type);

    /// <summary>
    /// Does <paramref name="value"/> conform to <paramref name="type"/>? Returns
    /// false for an unknown type. Null handling (required vs optional) is the
    /// caller's concern — this only judges a present value.
    /// </summary>
    public static bool IsValidValue(string type, object value) =>
        Lookup.TryGetValue(type, out var spec) && spec.IsValidValue(value);

    /// <summary>
    /// Canonical names whose values are stored as JSON numbers rather than JSON strings.
    /// </summary>
    /// <remarks>
    /// jsonb compares by type before value, so a stored number never equals a string carrying the
    /// same digits. Anything comparing a caller's text against stored content has to know which of
    /// the two it is looking at, and the schema is the only thing that does.
    /// </remarks>
    private static readonly HashSet<string> NumericCanonical =
        new(StringComparer.OrdinalIgnoreCase) { "int", "decimal", "money" };

    /// <summary>Is a value of this type stored as a JSON number? Unknown types are not.</summary>
    public static bool IsNumericType(string? type) =>
        type is not null
        && Lookup.TryGetValue(type, out var spec)
        && NumericCanonical.Contains(spec.Name);

    /// <summary>The admin editor hint for a type, or <c>text</c> if unknown.</summary>
    public static string EditorHintFor(string type) =>
        Lookup.TryGetValue(type, out var spec) ? spec.EditorHint : "text";

    // --- JSON-aware primitive checks (shared by every validator) --------------
    // These preserve the exact acceptance the live ContentValidatorService had,
    // so moving to the registry changes no behaviour for existing types.

    private static string? AsString(object value) => value switch
    {
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
        _ => null,
    };

    private static bool IsString(object value) => AsString(value) is not null;

    private static bool IsInteger(object value)
    {
        if (value is int or long or short or byte) return true;
        if (value is JsonElement { ValueKind: JsonValueKind.Number } je) return je.TryGetInt32(out _);
        if (value is string s) return int.TryParse(s, out _);
        return false;
    }

    private static bool IsBoolean(object value)
    {
        if (value is bool) return true;
        if (value is JsonElement { ValueKind: JsonValueKind.True or JsonValueKind.False }) return true;
        if (value is string s) return bool.TryParse(s, out _);
        return false;
    }

    private static bool IsDateTime(object value)
    {
        if (value is DateTime or DateTimeOffset) return true;
        if (value is JsonElement { ValueKind: JsonValueKind.String } je) return je.TryGetDateTime(out _);
        if (value is string s) return DateTime.TryParse(s, out _);
        return false;
    }

    private static bool IsDecimal(object value)
    {
        if (value is decimal or double or float or int or long) return true;
        if (value is JsonElement { ValueKind: JsonValueKind.Number } je) return je.TryGetDecimal(out _);
        if (value is string s) return decimal.TryParse(s, out _);
        return false;
    }

    private static bool IsArray(object value)
    {
        if (value is string) return false;
        if (value is System.Collections.IDictionary) return false;
        if (value is System.Collections.IEnumerable) return true;
        return value is JsonElement { ValueKind: JsonValueKind.Array };
    }

    private static bool IsObject(object value)
    {
        if (value is string or int or bool or DateTime or decimal) return false;
        if (IsArray(value)) return false;
        if (value is IDictionary<string, object>) return true;
        if (value is JsonElement { ValueKind: JsonValueKind.Object }) return true;
        return value.GetType().IsClass;
    }

    // A json field holds an arbitrary structured value: an object or an array.
    private static bool IsJson(object value) => IsObject(value) || IsArray(value);

    private static bool IsAbsoluteUrl(string s) =>
        Uri.TryCreate(s, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static bool IsTime(string s) =>
        TimeOnly.TryParse(s, out _) || TimeSpan.TryParse(s, out _);
}
