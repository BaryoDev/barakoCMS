using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using Marten;
using Marten.Linq.MatchesSql;
using Microsoft.AspNetCore.Http;

namespace BarakoCMS.Files;

/// <summary>One entry whose data references a file.</summary>
public class FileUsageRow
{
    public Guid Id { get; set; }
    public string ContentType { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string Status { get; set; } = string.Empty;
}

/// <summary>
/// Finds the entries whose data references a file.
/// </summary>
/// <remarks>
/// Content has no typed file field (#141 gave it a reference to another entry, not to a file), so a
/// file reference is whatever string an editor put in a field: the id on its own, the download URL
/// a client built from it, or, on an object store, the object's public URL. The first two carry
/// the id and the third carries the storage key, so both are matched as substrings of the entry's
/// data. That also catches a <c>?w=</c> variant URL, whose key is the parent's key plus a suffix.
///
/// It is a sequential scan over the tenant's entries. That is the right trade for an editor asking
/// about one file before deleting it, and the wrong one for anything on a hot path, which is why
/// the delete only runs it when the caller has not said <c>?force=true</c>.
/// </remarks>
internal static class FileUsage
{
    private const string Sql =
        "((d.data -> 'Data')::text ILIKE '%' || ? || '%' OR (d.data -> 'Data')::text ILIKE '%' || ? || '%')";

    public static IQueryable<Content> Referencing(IQuerySession session, StoredFile file)
    {
        var id = EscapeLike(file.Id.ToString());

        // A key with no stem would make the second pattern '%%', which matches every entry. The
        // upload always writes {guid:N}{ext}, so this is belt and braces rather than a live case.
        var stem = Path.GetFileNameWithoutExtension(file.StorageKey);
        var key = string.IsNullOrEmpty(stem) ? id : EscapeLike(stem);

        return session.Query<Content>()
            .Where(c => c.MatchesSql(Sql, id, key))
            .OrderByDescending(c => c.UpdatedAt);
    }

    /// <summary>
    /// The rows for a page of entries, each title put through the same two checks as
    /// <c>GET /api/contents</c>.
    /// </summary>
    /// <remarks>
    /// Every entry is listed whatever the caller may read of it, because the count is what stops a
    /// delete and a file used by an entry the caller cannot see is still used. What the entry says
    /// is another matter: the title is filled in only when the caller holds read on the type and
    /// the sensitivity scrub leaves the field, and a Hidden entry names its type to nobody but
    /// SuperAdmin, as on the content list.
    /// </remarks>
    public static async Task<List<FileUsageRow>> RowsAsync(
        IReadOnlyList<Content> entries,
        User? caller,
        IPermissionResolver permissions,
        ISensitivityService sensitivity,
        HttpContext http,
        CancellationToken ct)
    {
        var rows = new List<FileUsageRow>(entries.Count);

        foreach (var entry in entries)
        {
            var row = new FileUsageRow
            {
                Id = entry.Id,
                ContentType = entry.ContentType,
                Status = entry.Status.ToString(),
            };

            var data = new Dictionary<string, object>(entry.Data);
            if (await sensitivity.ApplyAsync(entry.ContentType, entry.Sensitivity, data, http, ct))
            {
                row.ContentType = "HIDDEN";
            }

            if (caller is not null
                && await permissions.CanPerformActionAsync(caller, entry.ContentType, "read", entry, ct))
            {
                row.Title = EntryTitle(data);
            }

            rows.Add(row);
        }

        return rows;
    }

    /// <summary>The same candidates, in the same order, the SEO fallback and the admin use to label an entry.</summary>
    private static string? EntryTitle(IReadOnlyDictionary<string, object> data)
    {
        foreach (var candidate in new[] { "Title", "Name", "DisplayName", "Label", "Subject", "Heading" })
        {
            foreach (var (field, value) in data)
            {
                if (!string.Equals(field, candidate, StringComparison.OrdinalIgnoreCase)) continue;

                var text = value?.ToString();
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
        }

        return null;
    }

    private static string EscapeLike(string term) => term
        .Replace("\\", "\\\\")
        .Replace("%", "\\%")
        .Replace("_", "\\_");
}
