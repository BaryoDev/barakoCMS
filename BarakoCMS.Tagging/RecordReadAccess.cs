using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using Marten;

namespace BarakoCMS.Tagging;

/// <summary>
/// Whether the caller may read a record a term sits on. The module never looks a record up for its
/// own sake, but a list of record ids is a list of records, so the read permission the content
/// endpoints ask of the same record is asked here too.
/// </summary>
internal sealed class RecordReadAccess(IQuerySession session, IPermissionResolver permissions, User user)
{
    private readonly Dictionary<string, bool> _types = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<RecordReadAccess?> ForCallerAsync(
        IQuerySession session, IPermissionResolver permissions, System.Security.Claims.ClaimsPrincipal principal, CancellationToken ct)
    {
        if (!Guid.TryParse(principal.FindFirst("UserId")?.Value, out var userId)) return null;
        return await session.LoadAsync<User>(userId, ct) is { } user
            ? new RecordReadAccess(session, permissions, user)
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
        return await session.LoadAsync<Content>(id, ct) is not { } entry
            || await permissions.CanPerformActionAsync(user, entry.ContentType, "read", entry, ct);
    }
}
