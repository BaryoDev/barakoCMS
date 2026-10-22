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

    public static async Task<int> CountAsync(
        IQuerySession session, LinesDefinition lines, Guid headerId, CancellationToken ct) =>
        (int)await ChildQuery(session, lines, headerId).CountAsync(ct);

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
        var definitions = await session.Query<ContentTypeDefinition>().ToListAsync(ct);

        var own = definitions.FirstOrDefault(d => barakoCMS.Core.ContentTypeName.Normalize(d.Name) == type);
        if (own?.Lines is { TotalField: not null } ownLines)
        {
            var stored = await ChildrenAsync(session, ownLines, existing.Id, CheckCap + 1, ct);
            errors.AddRange(stored.Count > CheckCap
                ? [$"The entry has more than {CheckCap} lines, so its total cannot be checked."]
                : Mismatches(ownLines, newData, stored.Select(c => (IDictionary<string, object>)c.Data)));
        }

        foreach (var header in definitions.Where(d =>
            d.Lines is { TotalField: not null } l && barakoCMS.Core.ContentTypeName.Normalize(l.ContentType) == type))
        {
            var lines = header.Lines!;
            var parents = new[] { Text(existing.Data, lines.ParentField), Text(newData, lines.ParentField) }
                .Where(p => p is not null).Select(p => p!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var parentText in parents)
            {
                if (!Guid.TryParse(parentText, out var parentId)) continue;
                var parent = await session.LoadAsync<barakoCMS.Models.Content>(parentId, ct);
                if (parent is null) continue;

                var stored = await ChildrenAsync(session, lines, parentId, CheckCap + 1, ct);
                var set = stored.Where(c => c.Id != existing.Id).Select(c => (IDictionary<string, object>)c.Data).ToList();
                if (string.Equals(Text(newData, lines.ParentField), parentText, StringComparison.OrdinalIgnoreCase))
                    set.Add(newData);

                errors.AddRange(Mismatches(lines, parent.Data, set).Select(e => $"Header {parentId}: {e}"));
            }
        }

        return errors;
    }

    /// <summary>The number a field holds, or null when it is absent or not a number.</summary>
    private static decimal? Read(IDictionary<string, object>? data, string field) =>
        decimal.TryParse(Text(data, field), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static string? Text(IDictionary<string, object>? data, string field) =>
        data?.FirstOrDefault(kv => string.Equals(kv.Key, field, StringComparison.OrdinalIgnoreCase)).Value?.ToString();
}
