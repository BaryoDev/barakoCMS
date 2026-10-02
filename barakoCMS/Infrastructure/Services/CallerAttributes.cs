using System.Text.Json;

namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// The <c>$CURRENT_USER.&lt;name&gt;</c> variables of a permission condition, read from the caller's
/// member profile in the current tenant.
/// </summary>
/// <remarks>
/// <see cref="ConditionEvaluator"/> and <see cref="PermissionPredicateCompiler"/> both resolve a
/// variable through this class, so the two cannot read a name differently.
///
/// A variable is only one as the whole of a scalar expected value. Inside an <c>_in</c> or
/// <c>_nin</c> list it stays text, which is where <c>$CURRENT_USER</c> has always drawn the line.
/// </remarks>
internal static class CallerAttributes
{
    public const string Prefix = "$CURRENT_USER.";

    public const int MaxAttributes = 32;

    public const int MaxNameLength = 64;

    public const int MaxValueLength = 256;

    public static bool IsReference(object? expected) =>
        expected is string text && text.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// The caller's value for the attribute a variable names. False for a name the profile does not
    /// hold and for a value that is null, empty or white space, so the comparison denies whatever
    /// its operator is: "not equal to nothing" would otherwise select every row.
    /// </summary>
    public static bool TryResolve(
        string reference, IReadOnlyDictionary<string, string>? profile, out string value)
    {
        value = string.Empty;

        if (profile is null || !reference.StartsWith(Prefix, StringComparison.Ordinal))
            return false;

        if (!profile.TryGetValue(reference[Prefix.Length..], out var found) || string.IsNullOrWhiteSpace(found))
            return false;

        value = found;
        return true;
    }

    /// <summary>Whether any comparison in these conditions is against a variable.</summary>
    public static bool Mentioned(Dictionary<string, object> conditions)
    {
        foreach (var comparison in conditions.Values)
        {
            switch (comparison)
            {
                case JsonElement { ValueKind: JsonValueKind.Object } element:
                    if (element.EnumerateObject().Any(p =>
                            p.Value.ValueKind == JsonValueKind.String && IsReference(p.Value.GetString())))
                        return true;
                    break;

                case Dictionary<string, object> operators:
                    if (operators.Values.Any(v => IsReference(
                            v is JsonElement { ValueKind: JsonValueKind.String } text ? text.GetString() : v)))
                        return true;
                    break;
            }
        }

        return false;
    }

    /// <summary>What is wrong with a profile a request sent, or null.</summary>
    public static string? ProfileError(Dictionary<string, string>? profile)
    {
        if (profile is null)
            return null;

        if (profile.Count > MaxAttributes)
            return $"A profile holds at most {MaxAttributes} attributes.";

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, value) in profile)
        {
            if (!IsName(name))
                return "A profile attribute name starts with a letter and holds only letters, digits "
                    + $"and underscores, {MaxNameLength} characters at most.";

            if (!seen.Add(name))
                return "Two profile attribute names differ only by case. Names are case sensitive, so keep one.";

            if (string.IsNullOrWhiteSpace(value))
                return "A profile attribute needs a value. Leave the attribute out to remove it.";

            if (value.Length > MaxValueLength)
                return $"A profile attribute value holds at most {MaxValueLength} characters.";
        }

        return null;
    }

    private static bool IsName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength || !char.IsAsciiLetter(name[0]))
            return false;

        return name.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_');
    }
}
