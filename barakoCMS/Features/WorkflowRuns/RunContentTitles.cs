using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using barakoCMS.Infrastructure.Audit;
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
/// An erased entry is simply not there. A title above Public is recorded in the sensitive read audit,
/// once per entry per request, as the entry read would be. The entries are read in one query whatever the page holds.
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

        var reads = new List<SensitiveRead>();
        var definitions = new Dictionary<string, ContentTypeDefinition?>();
        foreach (var entry in entries)
        {
            if (!await branches.MayAccessAsync(caller, entry, ct)
                || !await permissions.CanPerformActionAsync(user, entry.ContentType, "read", entry, ct))
            {
                continue;
            }

            var scrubbed = new Dictionary<string, object>(entry.Data);
            if (await sensitivity.ApplyAsync(entry, scrubbed, http, ct)) continue;

            if (TitleOf(entry.Data, scrubbed) is not { } title) continue;

            titles[entry.Id] = title;
            if (!definitions.TryGetValue(entry.ContentType, out var definition))
            {
                definition = definitions[entry.ContentType] = await AccountContentTypes.ResolveAsync(session, entry.ContentType, ct);
            }

            var shown = scrubbed.Where(p => string.Equals(p.Key, "title", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(p => p.Key, p => p.Value);
            reads.Add(await SensitiveReadAudit.DescribeAsync(entry, definition, shown, sensitivity, http, ct));
        }

        await SensitiveReadAudit.RecordAsync(http, reads, ct);
        return titles;
    }

    /// <summary>
    /// The title as the caller's rules left it, or null when it is gone, changed, not text, blank or
    /// never there. Control, format and line or paragraph separator characters are removed, so the
    /// text cannot carry a bidirectional override into a console.
    /// </summary>
    internal static string? TitleOf(IDictionary<string, object> stored, IDictionary<string, object> scrubbed)
    {
        foreach (var (key, value) in scrubbed)
        {
            if (!string.Equals(key, "title", StringComparison.OrdinalIgnoreCase)) continue;

            if (TextOf(value) is not { } text) return null;

            var original = TextOf(stored.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.Ordinal)).Value);
            if (original != text) return null;

            var clean = Clip(Strip(text).Trim());
            return string.IsNullOrWhiteSpace(clean) ? null : clean;
        }

        return null;
    }

    private static string? TextOf(object? value) => value switch
    {
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
        _ => null,
    };

    private static string Strip(string text)
    {
        var kept = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator) continue;
            kept.Append(rune.ToString());
        }

        return kept.ToString();
    }

    private static string Clip(string text)
    {
        if (text.Length <= MaxLength) return text;
        var end = char.IsHighSurrogate(text[MaxLength - 1]) ? MaxLength - 1 : MaxLength;
        return text[..end];
    }
}
