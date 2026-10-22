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

    /// <summary>The longest <c>branches</c> value: 32 ids and the commas between them, with room for spaces.</summary>
    public const int MaxBranchesValueLength = 1200;

    /// <summary>The request's branch, read from the reserved profile attribute <c>branch</c>.</summary>
    public const string CurrentBranch = "$CURRENT_BRANCH";

    public static bool IsReference(object? expected) =>
        expected is string text
        && (text.StartsWith(Prefix, StringComparison.Ordinal)
            || text.Equals(CurrentBranch, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The caller's value for the attribute a variable names. False for a name the profile does not
    /// hold and for a value that is null, empty or white space, so the comparison denies whatever
    /// its operator is: "not equal to nothing" would otherwise select every row.
    /// </summary>
    public static bool TryResolve(
        string reference, IReadOnlyDictionary<string, string>? profile, out string value)
    {
        value = string.Empty;

        if (profile is null)
            return false;

        string name;
        if (reference.Equals(CurrentBranch, StringComparison.OrdinalIgnoreCase))
            name = CallerBranches.BranchAttribute;
        else if (reference.StartsWith(Prefix, StringComparison.Ordinal))
            name = reference[Prefix.Length..];
        else
            return false;

        if (!profile.TryGetValue(name, out var found) || string.IsNullOrWhiteSpace(found))
            return false;

        value = found;
        return true;
    }

    /// <summary>
    /// Whether a field's value is one a caller attribute can be compared with. A list or an object
    /// is not, and matches nothing.
    /// </summary>
    /// <remarks>
    /// The evaluator compares the text of both sides, and the text of a list read back from storage
    /// is the name of its type while the compiled predicate sees its JSON. For a value written into
    /// a rule that difference is old and its author's to avoid. A profile value is tenant data, so
    /// here it would let whoever sets a profile choose which of the two answers is the wrong one.
    /// </remarks>
    public static bool IsComparable(object? fieldValue) => fieldValue switch
    {
        null => true,
        string => true,
        JsonElement element => element.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object),
        System.Collections.IEnumerable => false,
        _ => true,
    };

    /// <summary>The same rule in SQL, for a jsonb expression holding one <c>?</c>.</summary>
    public static string ComparableSql(string extraction) =>
        $"jsonb_typeof({extraction}) NOT IN ('array', 'object')";

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

            if (BranchError(name, value) is { } branchError)
                return branchError;

            var limit = name == CallerBranches.BranchesAttribute ? MaxBranchesValueLength : MaxValueLength;
            if (value.Length > limit)
                return $"A profile attribute value holds at most {limit} characters.";

            // The name passed IsName above, so it is safe to say which one. A NUL is the case that
            // matters: jsonb refuses it, and the save would fail after the request was accepted.
            if (value.Any(char.IsControl))
                return $"The profile attribute '{name}' holds a control character, such as a line break or a NUL.";
        }

        return null;
    }

    /// <summary>
    /// <c>branch</c> is the request's and never stored; <c>branches</c> has to read back whole.
    /// </summary>
    private static string? BranchError(string name, string value)
    {
        if (name == CallerBranches.BranchAttribute)
            return "The profile attribute 'branch' is set by the request and cannot be stored.";

        if (name == CallerBranches.BranchesAttribute
            && CallerBranches.Parse(value).Count != value.Split(',').Length)
            return "The profile attribute 'branches' must be up to 32 branch ids separated by commas.";

        return null;
    }

    private static bool IsName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength || !char.IsAsciiLetter(name[0]))
            return false;

        return name.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_');
    }
}
