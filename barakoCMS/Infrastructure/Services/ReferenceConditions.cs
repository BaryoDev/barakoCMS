using System.Text.Json;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// A permission condition whose key follows one reference: <c>Class.InstructorUser</c> reads
/// <c>InstructorUser</c> off the entry the row's <c>Class</c> field points at.
/// </summary>
/// <remarks>
/// <see cref="PermissionResolver"/>, <see cref="PermissionPredicateCompiler"/>,
/// <see cref="ConditionEvaluator"/> and the role endpoints all read a key through this class, so no
/// two of them can split one differently.
///
/// A key holding a dot is a path and is never looked up in the row's own data. An entry write keeps
/// keys the type does not declare, so a row could carry a key spelled <c>Class.InstructorUser</c>,
/// and matching it would let whoever writes a row decide who reads it.
///
/// One hop. Every way a path can fail to resolve denies: a field that is not a declared reference,
/// a value that is not an id, an entry that is missing, of another type or not Public, one the
/// caller may not read, and a field the referenced type does not declare as Public.
/// </remarks>
internal static class ReferenceConditions
{
    /// <summary>
    /// The most referenced entries one request loads to answer per-entry checks. Past it a
    /// reference that was not already loaded denies, and a warning is logged.
    /// </summary>
    public const int MaxEntriesPerRequest = 200;

    /// <summary>
    /// The most referenced entries one condition may match when a list is answered in the
    /// database. Past it the condition matches nothing in that list, and a warning is logged.
    /// </summary>
    public const int MaxEntriesPerCondition = 1000;

    public const int MaxNameLength = 64;

    public static readonly IReadOnlyList<string> Operators = ["_eq", "_ne", "_in", "_nin"];

    public static bool IsPath(string key) => !key.StartsWith('$') && key.Contains('.');

    public static bool Mentioned(Dictionary<string, object>? conditions) =>
        conditions is not null && conditions.Keys.Any(IsPath);

    /// <summary>
    /// The reference field and the referenced entry's field a path names. False for anything but
    /// two names around one dot, which is also what refuses a second hop.
    /// </summary>
    public static bool TrySplit(string key, out string reference, out string field)
    {
        reference = string.Empty;
        field = string.Empty;

        if (!IsPath(key))
            return false;

        var dot = key.IndexOf('.');
        var first = key[..dot];
        var second = key[(dot + 1)..];

        if (!IsName(first) || !IsName(second))
            return false;

        reference = first;
        field = second;
        return true;
    }

    private static bool IsName(string name) =>
        name.Length is > 0 and <= MaxNameLength
        && char.IsAsciiLetter(name[0])
        && name.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_');

    /// <summary>The operator names of a condition's value, or null when it is not an object.</summary>
    public static IReadOnlyList<string>? OperatorNames(object? value) => value switch
    {
        JsonElement { ValueKind: JsonValueKind.Object } element =>
            element.EnumerateObject().Select(p => p.Name).ToList(),
        Dictionary<string, object> operators => operators.Keys.ToList(),
        _ => null,
    };

    /// <summary>
    /// The id a reference field holds. Only the hyphenated form, in either case, because that is
    /// what <see cref="In"/> can match in SQL: a value one of the two accepted and the other did
    /// not would be an entry a caller can open and not list.
    /// </summary>
    public static bool TryReadId(object? value, out Guid id)
    {
        id = Guid.Empty;

        var text = value switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => null,
        };

        return text is { Length: 36 } && Guid.TryParseExact(text, "D", out id);
    }

    /// <summary>The field a path follows, or null when the type does not declare it as a reference.</summary>
    public static FieldDefinition? ReferenceField(ContentTypeDefinition? definition, string name)
    {
        var field = definition?.Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.Ordinal));

        return field is not null
            && string.Equals(field.Type, "reference", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(field.ReferenceType)
            ? field
            : null;
    }

    /// <summary>
    /// Whether a condition may compare this field of the referenced type. Only a declared field
    /// whose sensitivity is Public: the rule's answer says something about the value, and the
    /// caller is not the one who chose to be told.
    /// </summary>
    public static bool IsComparable(ContentTypeDefinition? target, string name) =>
        target is not null
        && target.Fields.Any(f =>
            string.Equals(f.Name, name, StringComparison.Ordinal) && f.Sensitivity == SensitivityLevel.Public);

    public static Task<ContentTypeDefinition?> DefinitionAsync(
        IQuerySession session, string name, CancellationToken cancellationToken) =>
        session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name == name, cancellationToken);

    /// <summary>
    /// The definition a <c>referenceType</c> names: as written, then in its stored form, since a
    /// field may spell the type the way its author typed it.
    /// </summary>
    public static async Task<ContentTypeDefinition?> TargetDefinitionAsync(
        Func<string, Task<ContentTypeDefinition?>> definition, string referenceType)
    {
        var found = await definition(referenceType);
        if (found is not null)
            return found;

        var normalized = barakoCMS.Core.ContentTypeName.Normalize(referenceType);
        return normalized == referenceType ? null : await definition(normalized);
    }

    /// <summary>The rows whose reference field holds one of these ids, as a predicate.</summary>
    public static ReadPredicate In(string referenceField, IReadOnlyList<Guid> ids)
    {
        if (ids.Count == 0)
            return ReadPredicate.Nothing;

        var parameters = new List<object>(ids.Count + 1) { referenceField };
        parameters.AddRange(ids.Select(id => (object)id.ToString()));

        var placeholders = string.Join(", ", Enumerable.Repeat("?", ids.Count));

        return new ReadPredicate($"lower(d.data -> 'Data' ->> ?) IN ({placeholders})", parameters.ToArray());
    }
}
