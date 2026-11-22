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
    /// Ends one live session of the user and audits it in the same save. False when the user has no
    /// such live session or another call ended it first, so a caller cannot tell another person's
    /// session from one that does not exist, and only the call that ended it audits it.
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

        return await SessionRevocation.EndAsync(
            session, cache, userId, sessionId, ct, s => Audit(s, tenantSlug, actorId, actorName, userId, sessionId, ct));
    }

    /// <summary>
    /// Ends every live session of the user except <paramref name="keep"/>, and returns how many. Also
    /// revokes the user's unrevoked rows that have no session id, since a refresh of one starts a
    /// session of its own.
    /// </summary>
    public static async Task<int> EndAllAsync(
        IDocumentSession session, IMemoryCache? cache, string tenantSlug, Guid? actorId, string? actorName,
        Guid userId, Guid? keep, CancellationToken ct)
    {
        var ended = 0;
        var unmarkedDone = false;
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
                break;
            }

            foreach (var id in ids.Distinct())
            {
                var withUnmarked = !unmarkedDone;
                var revoked = await SessionRevocation.EndAsync(session, cache, userId, id, ct, async s =>
                {
                    await Audit(s, tenantSlug, actorId, actorName, userId, id, ct);
                    if (withUnmarked)
                    {
                        await RevokeUnmarkedAsync(s, userId, ct);
                    }
                });
                if (revoked)
                {
                    ended++;
                    unmarkedDone = true;
                }
            }
        }

        if (!unmarkedDone)
        {
            await RevokeUnmarkedAsync(session, userId, ct);
            await session.SaveChangesAsync(ct);
        }

        return ended;
    }

    private static async Task RevokeUnmarkedAsync(IDocumentSession session, Guid userId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var rows = await session.Query<RefreshToken>()
            .Where(t => t.UserId == userId && !t.IsRevoked && t.SessionId == null)
            .ToListAsync(ct);
        foreach (var row in rows)
        {
            row.IsRevoked = true;
            row.RevokedReason = SessionRevocation.Reason;
            row.RevokedAt = now;
            session.Store(row);
        }
    }

    private static Task Audit(
        IDocumentSession session, string tenantSlug, Guid? actorId, string? actorName,
        Guid userId, Guid sessionId, CancellationToken ct) =>
        AuditLog.RecordAsync(session, tenantSlug, RevokedAction, actorId, actorName,
            targetType: "User", targetId: userId.ToString(),
            metadata: new Dictionary<string, object> { ["sessionId"] = sessionId.ToString() }, ct: ct);

    /// <summary>The session the request came from, read from its <c>sid</c> claim.</summary>
    public static Guid? CurrentOf(System.Security.Claims.ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirst(SessionRevocation.ClaimType)?.Value, out var sid) ? sid : null;
}
