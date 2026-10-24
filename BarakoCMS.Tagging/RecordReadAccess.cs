using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using System.Security.Claims;
using Marten;

namespace BarakoCMS.Tagging;

/// <summary>
/// Whether the caller may read a record a term sits on. The module never looks a record up for its
/// own sake, but a list of record ids is a list of records, so the read permission the content
/// endpoints ask of the same record is asked here too.
/// </summary>
internal sealed class RecordReadAccess(
    IQuerySession session, IPermissionResolver permissions, IBranchScopeGuard branches, ClaimsPrincipal principal, User user)
{
    private readonly Dictionary<string, bool> _types = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<RecordReadAccess?> ForCallerAsync(
        IQuerySession session, IPermissionResolver permissions, IBranchScopeGuard branches, ClaimsPrincipal principal, CancellationToken ct)
    {
        if (!Guid.TryParse(principal.FindFirst("UserId")?.Value, out var userId)) return null;
        return await session.LoadAsync<User>(userId, ct) is { } user
            ? new RecordReadAccess(session, permissions, branches, principal, user)
            : null;
    }

    /// <summary>Whether the caller may read entries of this content type at all.</summary>
    public async Task<bool> CanReadTypeAsync(string contentType, CancellationToken ct)
    {
        if (!_types.TryGetValue(contentType, out var allowed))
            _types[contentType] = allowed = await permissions.CanPerformActionAsync(user, contentType, "read", null, ct);
        return allowed;
    }

    /// <summary>Whether the caller may read this record: the type, and the entry itself when it is a content entry.</summary>
    public async Task<bool> CanReadAsync(string contentType, string recordId, CancellationToken ct)
    {
        if (!await CanReadTypeAsync(contentType, ct)) return false;
        if (!Guid.TryParse(recordId, out var id)) return true;
        return await session.LoadAsync<Content>(id, ct) is not { } entry || await CanReadEntryAsync(entry, ct);
    }

    /// <summary>The assignments whose records the caller may read, in the order given. Entries are loaded in one query.</summary>
    public async Task<List<TermAssignment>> ReadableAsync(IReadOnlyList<TermAssignment> assignments, CancellationToken ct)
    {
        var typeReadable = new List<TermAssignment>();
        foreach (var assignment in assignments)
        {
            if (await CanReadTypeAsync(assignment.ContentType, ct)) typeReadable.Add(assignment);
        }

        var ids = typeReadable.Select(a => Guid.TryParse(a.RecordId, out var id) ? id : (Guid?)null)
            .OfType<Guid>().Distinct().ToArray();
        var entries = ids.Length == 0
            ? []
            : (await session.Query<Content>().Where(c => ids.Contains(c.Id)).ToListAsync(ct)).ToDictionary(c => c.Id);

        var readable = new List<TermAssignment>();
        foreach (var assignment in typeReadable)
        {
            if (!Guid.TryParse(assignment.RecordId, out var id) || !entries.TryGetValue(id, out var entry)
                || await CanReadEntryAsync(entry, ct))
                readable.Add(assignment);
        }

        return readable;
    }

    private async Task<bool> CanReadEntryAsync(Content entry, CancellationToken ct) =>
        await branches.MayAccessAsync(principal, entry, ct)
        && await permissions.CanPerformActionAsync(user, entry.ContentType, "read", entry, ct);
}
