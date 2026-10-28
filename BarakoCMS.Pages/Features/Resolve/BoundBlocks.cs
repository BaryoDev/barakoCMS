using System.Text.Json;
using System.Text.RegularExpressions;
using barakoCMS.Core.Interfaces;
using barakoCMS.Models;
using Marten;
using Marten.Linq.MatchesSql;

namespace BarakoCMS.Pages.Features.Resolve;

/// <summary>The entries one block's binding selects, for the block at <paramref name="Index"/> of a <c>blocks</c> field.</summary>
internal sealed record BoundBlock(string Field, int Index, Guid Binding, IReadOnlyList<PublicContentProjection> Entries);

/// <summary>
/// Materialises the bindings a served page's blocks name, so a renderer draws the page and its
/// blocks' entries from one answer instead of querying for each block itself.
/// </summary>
/// <remarks>
/// Read off the page as delivery projects it, so a <c>blocks</c> field that is not Public binds
/// nothing. Each entry is projected for an anonymous reader like any delivered entry: published,
/// document-Public, of a publicly deliverable type, Public fields only, references to entries a
/// reader may not see left out. A binding whose type stopped
/// being deliverable, or whose sort or conditions name a field that is no longer Public, selects
/// nothing rather than filtering or ordering on a value no reader may see.
///
/// Bounded for an anonymous route: at most <see cref="MaxBindings"/> blocks per page are
/// materialised, each at most <see cref="BlockBinding.MaxLimit"/> entries, one query each.
/// </remarks>
internal static class BoundBlocks
{
    public const int MaxBindings = 10;

    private const string BlocksType = "blocks";

    private const string FieldEqualsIgnoreCaseSql =
        "lower((SELECT e.value FROM jsonb_each(d.data -> 'Data') e WHERE lower(e.key) = lower(?) LIMIT 1) #>> '{}') = lower(?)";

    private static readonly Regex SafeFieldName = new("^[A-Za-z][A-Za-z0-9]*$", RegexOptions.Compiled);

    public static async Task<IReadOnlyList<BoundBlock>> MaterializeAsync(
        IQuerySession session,
        IPublicContentProjector projector,
        ContentTypeDefinition pageType,
        PublicContentProjection page,
        CancellationToken ct)
    {
        var wanted = Wanted(pageType, page).Take(MaxBindings).ToList();
        if (wanted.Count == 0)
        {
            return [];
        }

        var ids = wanted.Select(w => w.Binding).Distinct().ToArray();
        var entries = new Dictionary<Guid, IReadOnlyList<PublicContentProjection>>();
        foreach (var binding in await session.LoadManyAsync<BlockBinding>(ct, ids))
        {
            entries[binding.Id] = await EntriesAsync(session, projector, binding, ct);
        }

        return wanted
            .Where(w => entries.ContainsKey(w.Binding))
            .Select(w => new BoundBlock(w.Field, w.Index, w.Binding, entries[w.Binding]))
            .ToList();
    }

    private static IEnumerable<(string Field, int Index, Guid Binding)> Wanted(ContentTypeDefinition pageType, PublicContentProjection page)
    {
        foreach (var field in pageType.Fields.Where(f => string.Equals(f.Type, BlocksType, StringComparison.OrdinalIgnoreCase)))
        {
            var value = page.Data.FirstOrDefault(kv => string.Equals(kv.Key, field.Name, StringComparison.OrdinalIgnoreCase)).Value;
            if (value is null)
            {
                continue;
            }

            var element = value is JsonElement je ? je : JsonSerializer.SerializeToElement(value);
            if (element.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var index = 0;
            foreach (var block in element.EnumerateArray())
            {
                if (block.ValueKind == JsonValueKind.Object
                    && block.TryGetProperty("binding", out var binding)
                    && binding.ValueKind == JsonValueKind.String
                    && Guid.TryParse(binding.GetString(), out var id))
                {
                    yield return (field.Name, index, id);
                }

                index++;
            }
        }
    }

    private static async Task<IReadOnlyList<PublicContentProjection>> EntriesAsync(
        IQuerySession session, IPublicContentProjector projector, BlockBinding binding, CancellationToken ct)
    {
        var lowered = binding.ContentType.ToLower();
        var definition = await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name.ToLower() == lowered, ct);
        if (!projector.IsDeliverable(definition))
        {
            return [];
        }

        var sortField = binding.Sort?.TrimStart('-');
        var named = (binding.Where?.Keys ?? Enumerable.Empty<string>()).Concat(sortField is null ? [] : [sortField]);
        if (named.Any(n => PublicField(definition!, n) is null))
        {
            return [];
        }

        var name = definition!.Name;
        var query = session.Query<Content>()
            .Where(c => c.ContentType == name && c.Status == ContentStatus.Published && c.Sensitivity == SensitivityLevel.Public);

        foreach (var (field, value) in binding.Where ?? new())
        {
            var canonical = PublicField(definition, field)!;
            query = query.Where(c => c.MatchesSql(FieldEqualsIgnoreCaseSql, canonical, value));
        }

        // A field name reaches SQL as text here, as in core's own delivery sort, so only the type's
        // own spelling of a Public field of letters and digits is let through.
        query = sortField is not null && PublicField(definition, sortField) is { } sort && SafeFieldName.IsMatch(sort)
            ? query.OrderBySql(
                $"(SELECT e.value FROM jsonb_each(d.data -> 'Data') e WHERE lower(e.key) = lower('{sort}') LIMIT 1) "
                + $"{(binding.Sort!.StartsWith('-') ? "DESC" : "ASC")} NULLS LAST, d.id")
            : query.OrderByDescending(c => c.CreatedAt).ThenBy(c => c.Id);

        var docs = await query.Take(Math.Clamp(binding.Limit, 1, BlockBinding.MaxLimit)).ToListAsync(ct);

        // As the page itself is projected, so a reference to an entry no reader may see is left out
        // and a file field is answered, the way core delivery answers both.
        var projected = new List<PublicContentProjection>(docs.Count);
        foreach (var doc in docs)
        {
            if (await projector.ProjectAsync(doc, definition, ct) is { } entry)
            {
                projected.Add(entry);
            }
        }

        return projected;
    }

    /// <summary>
    /// A field delivery serves: Public, and not a token, whose value the server keeps and delivery
    /// never returns. Ordering or filtering on any other would answer questions about a value no
    /// reader may see.
    /// </summary>
    internal static string? PublicField(ContentTypeDefinition definition, string name) =>
        definition.Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)
            && f.Sensitivity == SensitivityLevel.Public
            && !barakoCMS.Core.Validation.PublicFields.IsOwnerHidden(f)
            && !string.Equals(f.Type, "token", StringComparison.OrdinalIgnoreCase))?.Name;
}
