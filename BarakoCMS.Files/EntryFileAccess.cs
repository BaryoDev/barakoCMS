using System.Security.Claims;
using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
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
    private const string FieldHoldsId =
        "exists (select 1 from jsonb_each_text(d.data -> 'Data') e where lower(e.key) = lower(?) and lower(e.value) = lower(?))";

    public static async Task<bool> MayReadViaEntryAsync(
        StoredFile file,
        ClaimsPrincipal principal,
        User caller,
        IQuerySession session,
        IPermissionResolver permissions,
        ISensitivityService sensitivity,
        CancellationToken ct)
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
                    if (await permissions.CanPerformActionAsync(caller, entry.ContentType, "read", entry, ct)
                        && await FieldShownAsync(entry, name, id, principal, sensitivity, ct))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the entry read path still shows this caller the file field: the read rule's
    /// ReadableFields and the sensitivity scrub run over a copy, and the id must survive both.
    /// </summary>
    private static async Task<bool> FieldShownAsync(
        Content entry, string field, string id, ClaimsPrincipal principal, ISensitivityService sensitivity, CancellationToken ct)
    {
        var shown = new Dictionary<string, object>(entry.Data);
        await sensitivity.ApplyAsync(entry, shown, new DefaultHttpContext { User = principal }, ct);
        return shown.Any(kv =>
            string.Equals(kv.Key, field, StringComparison.OrdinalIgnoreCase)
            && string.Equals(kv.Value?.ToString(), id, StringComparison.OrdinalIgnoreCase));
    }
}
