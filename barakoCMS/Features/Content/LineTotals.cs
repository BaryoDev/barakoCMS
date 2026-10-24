using System.Globalization;
using Marten;
using Marten.Linq.MatchesSql;
using barakoCMS.Models;

namespace barakoCMS.Features.Content;

/// <summary>
/// The lines of a header, and the rule that a header's total equals the sum of its lines' amounts.
/// </summary>
/// <remarks>
/// One query shape for reading the lines and for checking the total, so the two cannot disagree
/// about which entries are a header's lines. Both run on a session, which filters by tenant.
/// </remarks>
internal static class LineTotals
{
    public const int MaxLines = 200;

    /// <summary>The most lines a total is added up over. More than this and the total cannot be checked.</summary>
    private const int CheckCap = 2000;

    private const string ChildrenSql =
        "EXISTS (SELECT 1 FROM jsonb_each_text(d.data -> 'Data') kv WHERE lower(kv.key) = lower(?) AND lower(kv.value) = ?)";

    private static IQueryable<barakoCMS.Models.Content> ChildQuery(IQuerySession session, LinesDefinition lines, Guid headerId)
    {
        var childType = lines.ContentType.ToLower();
        var parentField = lines.ParentField;
        var parentId = headerId.ToString().ToLowerInvariant();
        return session.Query<barakoCMS.Models.Content>()
            .Where(c => c.ContentType.ToLower() == childType && c.MatchesSql(ChildrenSql, parentField, parentId));
    }

    /// <summary>The header's lines, oldest first, at most <paramref name="take"/> of them.</summary>
    public static async Task<IReadOnlyList<barakoCMS.Models.Content>> ChildrenAsync(
        IQuerySession session, LinesDefinition lines, Guid headerId, int take, CancellationToken ct) =>
        await ChildQuery(session, lines, headerId).OrderBy(c => c.CreatedAt).Take(take).ToListAsync(ct);

    /// <summary>
    /// What stops <paramref name="header"/> holding its total, or empty when the type declares no
    /// total or the lines add up to it. A line with no amount, or one that is not a number, is
    /// refused by name rather than counted as 0. No lines add up to 0.
    /// </summary>
    public static List<string> Mismatches(
        LinesDefinition lines, IDictionary<string, object>? header, IEnumerable<IDictionary<string, object>> entries)
    {
        var errors = new List<string>();
        if (lines.TotalField is null || lines.LineAmountField is null) return errors;

        var sum = 0m;
        var n = 0;
        try
        {
            foreach (var data in entries)
            {
                n++;
                if (Read(data, lines.LineAmountField) is { } amount) sum += amount;
                else errors.Add($"Line {n}: '{lines.LineAmountField}' is missing or not a number.");
            }
        }
        catch (OverflowException)
        {
            return [$"The amounts in '{lines.LineAmountField}' are too large to add up."];
        }

        if (errors.Count > 0) return errors;

        var total = Read(header, lines.TotalField);
        if (total != sum)
        {
            errors.Add($"'{lines.TotalField}' is {total?.ToString(CultureInfo.InvariantCulture) ?? "missing"}, "
                + $"but the lines add up to {sum.ToString(CultureInfo.InvariantCulture)}.");
        }

        return errors;
    }

    /// <summary>
    /// The refusals for an update of <paramref name="existing"/> to <paramref name="newData"/>: when
    /// it is a header whose type declares a total, its stored lines are added up against the new
    /// data; when it is a line of such a type, each header it points at, before or after, is checked
    /// with this line as it would be.
    /// </summary>
    public static async Task<List<string>> CheckUpdateAsync(
        IQuerySession session, barakoCMS.Models.Content existing, Dictionary<string, object> newData, CancellationToken ct)
    {
        var errors = new List<string>();
        var type = barakoCMS.Core.ContentTypeName.Normalize(existing.ContentType);

        var own = await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name.ToLower() == type, ct);
        if (own?.Lines is { TotalField: not null } ownLines)
        {
            var stored = await ChildrenAsync(session, ownLines, existing.Id, CheckCap + 1, ct);
            errors.AddRange((stored.Count > CheckCap
                ? [$"The entry has more than {CheckCap} lines, so its total cannot be checked."]
                : Mismatches(ownLines, newData, stored.Select(c => (IDictionary<string, object>)c.Data)))
                .Select(e => $"Header {existing.Id}: {e}"));
        }

        errors.AddRange(await CheckLineAsync(session, type, existing.Id, existing.Data, newData, ct));
        return errors;
    }

    /// <summary>
    /// The refusals for a new entry of <paramref name="contentType"/> stored on its own: when it is
    /// a line of a type that declares a total, the header it points at must still add up with it.
    /// </summary>
    public static Task<List<string>> CheckCreateAsync(
        IQuerySession session, string contentType, IDictionary<string, object> data, CancellationToken ct) =>
        CheckLineAsync(session, barakoCMS.Core.ContentTypeName.Normalize(contentType), null, null, data, ct);

    private static async Task<List<string>> CheckLineAsync(
        IQuerySession session, string type, Guid? selfId, IDictionary<string, object>? oldData,
        IDictionary<string, object> newData, CancellationToken ct)
    {
        var errors = new List<string>();
        var headers = await session.Query<ContentTypeDefinition>()
            .Where(d => d.MatchesSql("d.data -> 'Lines' ->> 'TotalField' is not null"))
            .ToListAsync(ct);
        foreach (var header in headers.Where(d =>
            barakoCMS.Core.ContentTypeName.Normalize(d.Lines!.ContentType) == type))
        {
            var lines = header.Lines!;
            var oldParent = ParentId(oldData, lines.ParentField);
            var newParent = ParentId(newData, lines.ParentField);
            foreach (var parentId in new[] { oldParent, newParent }.Where(p => p is not null).Select(p => p!.Value).Distinct())
            {
                var parent = await session.LoadAsync<barakoCMS.Models.Content>(parentId, ct);
                if (parent is null) continue;

                var stored = await ChildrenAsync(session, lines, parentId, CheckCap + 1, ct);
                var set = stored.Where(c => c.Id != selfId).Select(c => (IDictionary<string, object>)c.Data).ToList();
                if (newParent == parentId) set.Add(newData);

                errors.AddRange(Mismatches(lines, parent.Data, set).Select(e => $"Header {parentId}: {e}"));
            }
        }

        return errors;
    }

    /// <summary>
    /// The header a line points at. Only the canonical text form counts, the one the lines query
    /// matches on, so what is checked, listed and added up is the same set of entries.
    /// </summary>
    private static Guid? ParentId(IDictionary<string, object>? data, string parentField) =>
        Guid.TryParseExact(Text(data, parentField), "D", out var id) ? id : null;

    /// <summary>The number a field holds, or null when it is absent or not a number.</summary>
    private static decimal? Read(IDictionary<string, object>? data, string field) =>
        decimal.TryParse(Text(data, field), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static string? Text(IDictionary<string, object>? data, string field) =>
        data?.FirstOrDefault(kv => string.Equals(kv.Key, field, StringComparison.OrdinalIgnoreCase)).Value?.ToString();
}
