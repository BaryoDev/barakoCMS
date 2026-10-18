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

    /// <summary>A file field marked FileFollowsEntry: the content type and the field name.</summary>
    internal readonly record struct FollowedField(string Type, string Name);

    /// <summary>
    /// Every FileFollowsEntry file field. One read of the definitions; a caller that checks many
    /// files in a request loads this once and passes it to <see cref="MayReadViaEntryAsync"/>.
    /// </summary>
    public static async Task<IReadOnlyList<FollowedField>> LoadFollowedFieldsAsync(IQuerySession session, CancellationToken ct)
    {
        var definitions = await session.Query<ContentTypeDefinition>().ToListAsync(ct);
        return definitions
            .SelectMany(d => d.Fields
                .Where(f => f.FileFollowsEntry && string.Equals(f.Type, "file", StringComparison.OrdinalIgnoreCase))
                .Select(f => new FollowedField(d.Name, f.Name)))
            .ToList();
    }

    // The match scans the type's entries for each followed field (jsonb_each_text over Data). A
    // lookup that does not scan needs an index or a reference table, which is a schema change.
    public static async Task<bool> MayReadViaEntryAsync(
        StoredFile file,
        ClaimsPrincipal principal,
        User caller,
        IQuerySession session,
        IPermissionResolver permissions,
        ISensitivityService sensitivity,
        IReadOnlyList<FollowedField>? followed,
        CancellationToken ct)
    {
        var id = file.Id.ToString("D");

        foreach (var (type, name) in followed ?? await LoadFollowedFieldsAsync(session, ct))
        {
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
