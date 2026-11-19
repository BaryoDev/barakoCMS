using barakoCMS.Infrastructure.Audit;
using barakoCMS.Models;
using Marten;
using Microsoft.Extensions.Caching.Memory;

namespace barakoCMS.Infrastructure.Auth;

/// <summary>One live session as the routes show it. Ids and addresses of the person's own devices only.</summary>
internal sealed record SessionItem(
    Guid Id, string? Device, string? SignInMethod, string? SignInIp, string? LastSeenIp,
    DateTime StartedAt, DateTime LastSeenAt, bool Current);

/// <summary>The paged list and the audited end behind the session routes.</summary>
/// <remarks>
/// Sessions span tenants, so nothing here filters by tenant. The routes that reach another user's
/// sessions are limited to a global SuperAdmin for that reason. The audit entry is written to the
/// tenant the request came in on, and holds ids only, never the device text or an address.
/// </remarks>
internal static class SessionRoutes
{
    public const string RevokedAction = "auth.session.revoked";

    public const string RefusedMessage =
        "Only a platform administrator (SuperAdmin) can manage another user's sessions.";

    /// <summary>How many sessions one bulk end reads and ends at a time.</summary>
    private const int Batch = 100;

    private static IQueryable<RefreshToken> Live(IQuerySession session, Guid userId)
    {
        var now = DateTime.UtcNow;
        return session.Query<RefreshToken>()
            .Where(t => t.UserId == userId && !t.IsRevoked && t.ExpiresAt > now && t.SessionId != null);
    }

    public static async Task<PaginatedResponse<SessionItem>> ListAsync(
        IQuerySession session, Guid userId, Guid? current, PaginatedRequest request, CancellationToken ct)
    {
        var page = await Live(session, userId)
            .OrderByDescending(t => t.LastSeenAt)
            .ThenBy(t => t.Id)
            .ToPagedResponseAsync(request, ct);

        return page.Map(t => new SessionItem(
            t.SessionId!.Value, t.Device, t.SignInMethod, t.SignInIp, t.LastSeenIp,
            t.SessionStartedAt ?? t.CreatedAt, t.LastSeenAt ?? t.CreatedAt, t.SessionId == current));
    }

    /// <summary>
    /// Ends one live session of the user and audits it. False when the user has no such live
    /// session, so a caller cannot tell another person's session from one that does not exist.
    /// </summary>
    public static async Task<bool> EndAsync(
        IDocumentSession session, IMemoryCache? cache, string tenantSlug, Guid? actorId, string? actorName,
        Guid userId, Guid sessionId, CancellationToken ct)
    {
        var exists = await Live(session, userId).AnyAsync(t => t.SessionId == sessionId, ct);
        if (!exists)
        {
            return false;
        }

        await SessionRevocation.EndAsync(session, cache, userId, sessionId, ct);
        await Audit(session, tenantSlug, actorId, actorName, userId, sessionId, ct);
        return true;
    }

    /// <summary>Ends every live session of the user except <paramref name="keep"/>, and returns how many.</summary>
    public static async Task<int> EndAllAsync(
        IDocumentSession session, IMemoryCache? cache, string tenantSlug, Guid? actorId, string? actorName,
        Guid userId, Guid? keep, CancellationToken ct)
    {
        var ended = 0;
        while (true)
        {
            var live = Live(session, userId);
            if (keep is { } kept)
            {
                live = live.Where(t => t.SessionId != kept);
            }

            var ids = await live
                .Select(t => t.SessionId!.Value)
                .Take(Batch)
                .ToListAsync(ct);
            if (ids.Count == 0)
            {
                return ended;
            }

            foreach (var id in ids.Distinct())
            {
                await SessionRevocation.EndAsync(session, cache, userId, id, ct);
                await Audit(session, tenantSlug, actorId, actorName, userId, id, ct);
                ended++;
            }
        }
    }

    private static async Task Audit(
        IDocumentSession session, string tenantSlug, Guid? actorId, string? actorName,
        Guid userId, Guid sessionId, CancellationToken ct)
    {
        await AuditLog.RecordAsync(session, tenantSlug, RevokedAction, actorId, actorName,
            targetType: "User", targetId: userId.ToString(),
            metadata: new Dictionary<string, object> { ["sessionId"] = sessionId.ToString() }, ct: ct);
        await session.SaveChangesAsync(ct);
    }

    /// <summary>The session the request came from, read from its <c>sid</c> claim.</summary>
    public static Guid? CurrentOf(System.Security.Claims.ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirst(SessionRevocation.ClaimType)?.Value, out var sid) ? sid : null;
}
