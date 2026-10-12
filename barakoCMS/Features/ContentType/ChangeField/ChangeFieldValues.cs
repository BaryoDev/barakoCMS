using barakoCMS.Core.Validation;
using barakoCMS.Infrastructure.Jobs;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Features.ContentType.ChangeField;

/// <summary>How many entries hold a value a convert or a remove would drop.</summary>
internal static class ChangeFieldValues
{
    /// <summary>
    /// Counted before anything is changed, so the refusal can name it and the change can record it.
    /// Read in batches on the id, so the working set stays bounded on a large type.
    /// </summary>
    public static async Task<int> LostAsync(IDocumentSession session, ContentTypeDefinition definition, FieldChange change, CancellationToken ct)
    {
        var lowered = definition.Name.ToLower();
        var lost = 0;
        Guid? cursor = null;

        while (true)
        {
            var query = session.Query<barakoCMS.Models.Content>().Where(c => c.ContentType.ToLower() == lowered);
            if (cursor is { } after)
            {
                query = query.Where(c => c.Id > after);
            }

            var batch = await query.OrderBy(c => c.Id).Take(FieldChanges.BatchSize).ToListAsync(ct);
            if (batch.Count == 0)
            {
                return lost;
            }

            cursor = batch[^1].Id;
            foreach (var content in batch)
            {
                var key = FieldChanges.KeyOf(content.Data, change.Field);
                if (key is null || content.Data[key] is null)
                {
                    continue;
                }

                if (change.Kind == FieldChangeKind.Remove || FieldConversion.Convert(content.Data[key], change) is null)
                {
                    lost++;
                }
            }
        }
    }
}
