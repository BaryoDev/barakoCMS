using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using Marten;
using Marten.Linq.MatchesSql;

namespace BarakoCMS.Files;

/// <summary>
/// Read access to a private file through an entry that holds it in a <c>file</c> field marked
/// <see cref="FieldDefinition.FileFollowsEntry"/>. Download only: nothing here reaches delete, list
/// or edit.
/// </summary>
internal static class EntryFileAccess
{
    private const string FieldHoldsId = "(d.data -> 'Data') ->> ? = ?";

    public static async Task<bool> MayReadViaEntryAsync(
        StoredFile file, User caller, IQuerySession session, IPermissionResolver permissions, CancellationToken ct)
    {
        var id = file.Id.ToString("D");

        var definitions = await session.Query<ContentTypeDefinition>().ToListAsync(ct);
        foreach (var definition in definitions)
        {
            foreach (var field in definition.Fields.Where(f =>
                f.FileFollowsEntry && string.Equals(f.Type, "file", StringComparison.OrdinalIgnoreCase)))
            {
                var type = definition.Name;
                var name = field.Name;
                var entries = await session.Query<Content>()
                    .Where(c => c.ContentType == type && c.MatchesSql(FieldHoldsId, name, id))
                    .ToListAsync(ct);

                foreach (var entry in entries)
                {
                    if (await permissions.CanPerformActionAsync(caller, entry.ContentType, "read", entry, ct))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }
}
