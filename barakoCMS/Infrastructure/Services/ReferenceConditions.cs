using System.Text.Json;
using System.Text.RegularExpressions;
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
    /// The most referenced entries one condition resolves to as a set of ids. A condition that
    /// leads to more has no set. A list of a named type is then filtered by a subquery where the
    /// database can answer the whole condition, and any other pass over many rows is refused.
    /// </summary>
    public const int MaxEntriesPerCondition = 1000;

    /// <summary>
    /// The most matches read to build a set when the caller's Read rules for the referenced type
    /// have to be asked in memory. Each is a whole entry, read 500 at a time. A condition that
    /// matches more has no set, however few of them the caller may read.
    /// </summary>
    public const int MaxEntriesCompared = 2000;

    /// <summary>
    /// The most rows of a page the database filtered by subquery that load the entries they point
    /// at. A page holds at most half of this.
    /// </summary>
    public const int MaxEntriesPerRequest = 200;

    /// <summary>
    /// How many rows one scope checks by loading each entry a row points at, before a condition is
    /// resolved to a set. Ten, which is what a get by slug may have as candidates, so a get, an
    /// update, a transition, a preview and a slug lookup never meet the bound a pass over many
    /// rows does.
    /// </summary>
    public const int RowsReadSingly = 10;

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

    public static bool IsName(string name) =>
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
    /// The one spelling of an id a reference is followed through: eight, four, four, four and
    /// twelve hexadecimal digits with hyphens between, in either case. The same pattern is used
    /// in SQL by <see cref="InSubquery"/>.
    /// </summary>
    private const string IdPattern = "^[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}$";

    private static readonly Regex Id = new(
        IdPattern.Replace("$", "\\z"), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// The id a reference field holds. Only the hyphenated form, in either case, because that is
    /// what the list can match in SQL: a value one of the two accepted and the other did not would
    /// be an entry a caller can open and not list. The pattern is checked before the parse, since
    /// the parser also takes a part that starts with <c>0x</c> or a plus sign.
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

        return text is not null && Id.IsMatch(text) && Guid.TryParseExact(text, "D", out id);
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
    /// The names a <c>referenceType</c> may be stored under, in the order to try them: as written,
    /// then in its stored form, since a field may spell the type the way its author typed it.
    /// </summary>
    public static IReadOnlyList<string> TargetNames(string referenceType)
    {
        var normalized = barakoCMS.Core.ContentTypeName.Normalize(referenceType);
        return normalized == referenceType ? new[] { referenceType } : new[] { referenceType, normalized };
    }

    /// <summary>
    /// The comparison a condition makes on the referenced entry's field, as the predicate compiler
    /// writes it. Not compiled for a comparison the compiler declines.
    /// </summary>
    public static ReadPredicate Comparison(
        string field, object operators, Guid userId, IReadOnlyDictionary<string, string>? callerProfile) =>
        PermissionPredicateCompiler.Compile(
            [new PermissionRule { Enabled = true, Conditions = new Dictionary<string, object> { [field] = operators } }],
            userId,
            callerProfile);

    /// <summary>
    /// Whether the operators compare text the way a list can be answered in the database: text for
    /// <c>_eq</c> and <c>_ne</c>, a list of text holding at least one for <c>_in</c> and
    /// <c>_nin</c>. Asked of the compiler itself, so a role write and a check cannot disagree.
    /// </summary>
    public static bool ComparesText(object? operators) =>
        operators is not null && Comparison("Field", operators, Guid.Empty, null).Compiled;

    /// <summary>The rows whose reference field holds one of these ids, as a predicate.</summary>
    public static ReadPredicate In(string referenceField, IReadOnlyCollection<Guid> ids)
    {
        if (ids.Count == 0)
            return ReadPredicate.Nothing;

        var parameters = new List<object>(ids.Count + 1) { referenceField };
        parameters.AddRange(ids.Select(id => (object)id.ToString()));

        var placeholders = string.Join(", ", Enumerable.Repeat("?", ids.Count));

        return new ReadPredicate($"lower(d.data -> 'Data' ->> ?) IN ({placeholders})", parameters.ToArray());
    }

    private static readonly Regex CommandParameter = new(
        @"\$(?<position>\d+)|(?<![:\w])[:@](?<name>p\d+)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>
    /// The rows whose reference field holds the id of an entry the given query selects, for a
    /// condition that leads to more entries than a list of ids holds.
    /// </summary>
    /// <param name="selectIds">
    /// The command Marten built for "select the id of every referenced entry that satisfies the
    /// condition and that the caller may read", from a query on the scope's session. The tenant
    /// filter in it is the one Marten wrote, with its own parameter.
    /// </param>
    /// <returns>
    /// Null when the command cannot be carried over with certainty: every parameter it declares has
    /// to appear in its text, and the text may hold no <c>?</c> of its own. The caller then has no
    /// predicate, and refuses.
    /// </returns>
    /// <remarks>
    /// The inner query names its table <c>d</c>, as the outer one does. It reads nothing of the
    /// outer row, so the inner name hides the outer one inside the parentheses and nothing else.
    ///
    /// The reference value is cast to a uuid only when it has the spelling <see cref="TryReadId"/>
    /// reads, so a row holding anything else is not selected and cannot fail the cast.
    /// </remarks>
    public static ReadPredicate? InSubquery(string referenceField, Npgsql.NpgsqlCommand selectIds)
    {
        var text = selectIds.CommandText.Trim().TrimEnd(';').Trim();

        if (!text.StartsWith("select", StringComparison.OrdinalIgnoreCase) || text.Contains('?'))
            return null;

        var parameters = new List<object> { referenceField, referenceField };
        var used = new HashSet<int>();
        var unknown = false;

        var rendered = CommandParameter.Replace(text, match =>
        {
            var index = -1;

            if (match.Groups["position"].Success)
            {
                if (int.TryParse(match.Groups["position"].Value, out var position))
                    index = position - 1;
            }
            else
            {
                var name = match.Groups["name"].Value;
                for (var i = 0; i < selectIds.Parameters.Count; i++)
                {
                    if (selectIds.Parameters[i].ParameterName.TrimStart(':', '@') == name)
                    {
                        index = i;
                        break;
                    }
                }
            }

            if (index < 0 || index >= selectIds.Parameters.Count
                || selectIds.Parameters[index].Value is null or DBNull)
            {
                unknown = true;
                return match.Value;
            }

            used.Add(index);
            parameters.Add(selectIds.Parameters[index].Value!);
            return "?";
        });

        if (unknown || used.Count != selectIds.Parameters.Count)
            return null;

        return new ReadPredicate(
            "(CASE WHEN (d.data -> 'Data' ->> ?) ~ '" + IdPattern + "' "
          + "THEN (d.data -> 'Data' ->> ?)::uuid END) IN (" + rendered + ")",
            parameters.ToArray());
    }
}

/// <summary>
/// A pass over many rows met a condition that leads to more referenced entries than one request
/// follows.
/// </summary>
/// <remarks>
/// Thrown rather than answered with a denial for the rows past the bound, which would return a
/// list that is short and says nothing about being short. <c>MalformedRequestMiddleware</c> turns
/// it into a 403 carrying the message, which is written for the caller and names nothing but the
/// condition's key and the bound.
/// </remarks>
internal sealed class ReferenceConditionBoundException(string key) : InvalidOperationException(
    $"The permission condition '{key}' leads to more than {ReferenceConditions.MaxEntriesPerCondition} entries, "
  + "and a request that covers many rows follows at most that many. Read one entry at a time, name a content "
  + "type, or have the condition on the role narrowed.");
