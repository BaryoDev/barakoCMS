using barakoCMS.Core.Validation;
using barakoCMS.Models;
using Marten;
using ContentDoc = barakoCMS.Models.Content;

namespace barakoCMS.Features.Public;

/// <summary>
/// Leaves out of a delivered entry each reference to an entry that anonymous delivery would not
/// serve.
/// </summary>
/// <remarks>
/// Run on entries that <see cref="PublicDelivery.ToPublic"/> has already projected, so a reference
/// field the type does not mark Public is gone before this looks.
///
/// A target is kept when it is Published, document Public and of a publicly deliverable type: what
/// <see cref="PublicDelivery.ToPublic"/> asks of an entry, and so what <c>include</c> resolves. A
/// single reference to any other entry is removed from the data, and a list keeps the ids that pass,
/// in stored order. The same answer include gives, so a draft on the other end reads exactly like no
/// reference at all, and an id that names nothing reads the same way.
///
/// One read of the targets and one of their types, for every entry passed in together, and none
/// when no reference field holds a value. The ids read are ids the response already carries, so the
/// read grows with the response: a page of entries with at most
/// <see cref="ReferenceFields.MaxIds"/> ids in each list.
/// </remarks>
internal static class PublicReferenceFields
{
    public static Task<List<PublicContentResponse>> FilterAsync(
        IReadOnlyList<PublicContentResponse> items,
        ContentTypeDefinition definition,
        IQuerySession session,
        CancellationToken ct) =>
        FilterAsync(items.Select(item => (Item: item, Definition: definition)).ToList(), session, ct);

    /// <summary>The same entries in the same order, each read against its own type.</summary>
    public static async Task<List<PublicContentResponse>> FilterAsync(
        IReadOnlyList<(PublicContentResponse Item, ContentTypeDefinition Definition)> items,
        IQuerySession session,
        CancellationToken ct)
    {
        var fieldsOf = new Dictionary<ContentTypeDefinition, HashSet<string>>(ReferenceEqualityComparer.Instance);
        var ids = new HashSet<Guid>();
        var anyHeld = false;

        foreach (var (item, definition) in items)
        {
            if (!fieldsOf.TryGetValue(definition, out var fields))
                fieldsOf[definition] = fields = Names(definition);

            if (fields.Count == 0)
                continue;

            foreach (var (key, value) in item.Data)
            {
                if (value is null || !fields.Contains(key))
                    continue;

                anyHeld = true;
                foreach (var text in Texts(value))
                {
                    if (Guid.TryParse(text, out var id))
                        ids.Add(id);
                }
            }
        }

        if (!anyHeld)
            return items.Select(pair => pair.Item).ToList();

        var deliverable = await DeliverableAsync(session, ids, ct);
        return items.Select(pair => Filter(pair.Item, fieldsOf[pair.Definition], deliverable)).ToList();
    }

    /// <summary>The ids among these that name an entry anonymous delivery serves.</summary>
    private static async Task<HashSet<Guid>> DeliverableAsync(IQuerySession session, HashSet<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
            return [];

        var asked = ids.ToArray();
        var targets = await session.Query<ContentDoc>()
            .Where(c => c.Id.In(asked)
                        && c.Status == ContentStatus.Published
                        && c.Sensitivity == SensitivityLevel.Public)
            .Select(c => new { c.Id, c.ContentType })
            .ToListAsync(ct);

        if (targets.Count == 0)
            return [];

        var typeNames = targets.Select(t => t.ContentType).Distinct().ToArray();
        var open = (await session.Query<ContentTypeDefinition>()
                .Where(d => d.Name.In(typeNames) && d.IsPubliclyDeliverable)
                .Select(d => d.Name)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return targets.Where(t => open.Contains(t.ContentType)).Select(t => t.Id).ToHashSet();
    }

    private static PublicContentResponse Filter(PublicContentResponse item, HashSet<string> fields, HashSet<Guid> deliverable)
    {
        if (fields.Count == 0 || !item.Data.Keys.Any(fields.Contains))
            return item;

        var data = new Dictionary<string, object>(item.Data.Count);
        foreach (var (key, value) in item.Data)
        {
            if (value is null || !fields.Contains(key))
            {
                data[key] = value!;
                continue;
            }

            if (ReferenceFields.TryReadList(value, out var listed))
            {
                data[key] = listed.Where(text => IsDeliverable(text, deliverable)).ToList();
                continue;
            }

            var single = Texts(value).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(single) || IsDeliverable(single, deliverable))
                data[key] = value;
        }

        return item with { Data = data };
    }

    private static bool IsDeliverable(string text, HashSet<Guid> deliverable) =>
        Guid.TryParse(text, out var id) && deliverable.Contains(id);

    /// <summary>The text of each id a value holds: the elements of a list, or the value itself.</summary>
    private static IEnumerable<string> Texts(object value) =>
        ReferenceFields.TryReadList(value, out var listed) ? listed : [value.ToString() ?? string.Empty];

    private static HashSet<string> Names(ContentTypeDefinition definition) =>
        definition.Fields
            .Where(f => string.Equals(f.Type, "reference", StringComparison.OrdinalIgnoreCase))
            .Select(f => f.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
