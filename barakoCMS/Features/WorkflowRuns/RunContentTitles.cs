using System.Security.Claims;
using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Features.WorkflowRuns;

/// <summary>
/// The title of the entry each run points at, for the caller reading the runs.
/// </summary>
/// <remarks>
/// A title is a field value, so it goes through what reading the entry goes through: the branch
/// rule, the read permission on the type and that entry, and the sensitivity rules over a copy of
/// its data, the same calls <c>GET /api/contents/{id}</c> makes. A caller who may not read the entry,
/// or whose rules mask or drop the title, gets no title and cannot tell which of the two it was.
/// An erased entry is simply not there. The entries are read in one query whatever the page holds.
/// </remarks>
internal static class RunContentTitles
{
    private const int MaxLength = 200;

    /// <summary>The readable title by entry id. An id with no title the caller may read is absent.</summary>
    public static async Task<Dictionary<Guid, string>> ResolveAsync(
        IQuerySession session, HttpContext http, ClaimsPrincipal caller, IEnumerable<Guid> contentIds, CancellationToken ct)
    {
        var titles = new Dictionary<Guid, string>();
        var ids = contentIds.Distinct().ToArray();
        if (ids.Length == 0) return titles;

        if (await ActingCaller.LoadAsync(session, caller, ct) is not { } user) return titles;

        var entries = await session.LoadManyAsync<Models.Content>(ct, ids);

        var services = http.RequestServices;
        var branches = services.GetRequiredService<IBranchScopeGuard>();
        var permissions = services.GetRequiredService<IPermissionResolver>();
        var sensitivity = services.GetRequiredService<ISensitivityService>();

        foreach (var entry in entries)
        {
            if (!await branches.MayAccessAsync(caller, entry, ct)
                || !await permissions.CanPerformActionAsync(user, entry.ContentType, "read", entry, ct))
            {
                continue;
            }

            var scrubbed = new Dictionary<string, object>(entry.Data);
            if (await sensitivity.ApplyAsync(entry, scrubbed, http, ct)) continue;

            if (TitleOf(entry.Data, scrubbed) is { } title) titles[entry.Id] = title;
        }

        return titles;
    }

    /// <summary>The title as the caller's rules left it, or null when it is gone, changed or was never there.</summary>
    internal static string? TitleOf(IDictionary<string, object> stored, IDictionary<string, object> scrubbed)
    {
        foreach (var (key, value) in scrubbed)
        {
            if (!string.Equals(key, "title", StringComparison.OrdinalIgnoreCase) || value?.ToString() is not { Length: > 0 } text)
            {
                continue;
            }

            var original = stored.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.Ordinal)).Value?.ToString();
            return original == text ? Clip(text) : null;
        }

        return null;
    }

    private static string Clip(string text)
    {
        if (text.Length <= MaxLength) return text;
        var end = char.IsHighSurrogate(text[MaxLength - 1]) ? MaxLength - 1 : MaxLength;
        return text[..end];
    }
}
