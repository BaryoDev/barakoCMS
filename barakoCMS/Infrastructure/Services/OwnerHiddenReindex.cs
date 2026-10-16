using barakoCMS.Core.Validation;
using barakoCMS.Events;
using barakoCMS.Infrastructure.Jobs;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// Rebuilds the search text of a type's entries when a definition change starts hiding a field from
/// public readers. Entries already stored were indexed with that field's value in them.
/// </summary>
public static class OwnerHiddenReindex
{
    private const int Page = 200;

    /// <summary>The names of the fields the type lets an owner hide, taken before a definition is replaced.</summary>
    public static HashSet<string> HiddenNames(ContentTypeDefinition definition) =>
        definition.Fields.Where(PublicFields.IsOwnerHidden).Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether the definition now hides a field it did not hide before.</summary>
    public static bool Gained(HashSet<string> before, ContentTypeDefinition definition) =>
        HiddenNames(definition).Any(name => !before.Contains(name));

    /// <summary>
    /// Appends a search text rewrite to every indexed entry of the type whose text changes. The caller
    /// saves the session.
    /// </summary>
    public static async Task RebuildAsync(IDocumentSession session, ContentTypeDefinition definition, CancellationToken ct)
    {
        var lowered = definition.Name.ToLowerInvariant();
        var writer = new ContentWriter(session, new ContentSourcingPolicyService(session)) { WritesForFieldChange = true };
        Guid? cursor = null;

        while (true)
        {
            var query = session.Query<Content>().Where(c => c.ContentType.ToLower() == lowered);
            if (cursor is { } after)
            {
                query = query.Where(c => c.Id > after);
            }

            var batch = await query.OrderBy(c => c.Id).Take(Page).ToListAsync(ct);
            if (batch.Count == 0)
            {
                return;
            }

            foreach (var content in batch)
            {
                var rebuilt = FieldChanges.SearchText(content, definition, content.Data);
                if (rebuilt is null || string.Equals(rebuilt, content.SearchText, StringComparison.Ordinal))
                {
                    continue;
                }

                var field = definition.Fields.First(PublicFields.IsOwnerHidden);
                await writer.AppendAsync(
                    content,
                    new ContentFieldSensitivityChanged(
                        content.Id, field.Name, field.Sensitivity, field.Sensitivity, rebuilt, Guid.Empty, DateTime.UtcNow),
                    ct);
            }

            cursor = batch[^1].Id;
        }
    }
}
