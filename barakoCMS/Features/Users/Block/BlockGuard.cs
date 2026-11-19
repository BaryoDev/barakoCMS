using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.Users.Block;

/// <summary>The checks block and unblock share.</summary>
internal static class BlockGuard
{
    /// <summary>
    /// The caller's id, with no refusal, when they may act: a SuperAdmin holding the role globally,
    /// acting on someone else. Otherwise the refusal to send.
    /// </summary>
    public static async Task<(Guid Actor, int Status, string? Refusal)> ActorAsync(
        IQuerySession session, System.Security.Claims.ClaimsPrincipal caller, Guid targetId,
        string superAdminOnlyMessage, string selfMessage, CancellationToken ct)
    {
        Guid.TryParse(caller.FindFirst("UserId")?.Value, out var actorId);
        if (!await PlatformRoles.IsSuperAdminAsync(session, caller, ct))
            return (actorId, 403, superAdminOnlyMessage);

        return actorId == targetId ? (actorId, 400, selfMessage) : (actorId, 0, null);
    }

    /// <summary>The SuperAdmin role in the user's global roles or in any active membership.</summary>
    public static async Task<bool> HoldsSuperAdminAsync(IQuerySession session, User user, CancellationToken ct)
    {
        if (user.RoleIds.Contains(SystemRoles.SuperAdminRoleId))
            return true;

        var granted = await session.Query<Membership>()
            .Where(m => m.UserId == user.Id && m.Status == MembershipStatus.Active)
            .ToListAsync(ct);
        return granted.Any(m => m.RoleIds.Contains(SystemRoles.SuperAdminRoleId));
    }
}
