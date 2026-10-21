using FastEndpoints;
using Marten;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;

namespace barakoCMS.Features.Users.ResetMfa;

/// <summary>
/// POST /api/users/{userId}/mfa/reset: an administrator holding manage_users removes another user's
/// two-factor enrollment (secret, recovery codes and any pending setup), so someone who lost both
/// their authenticator and their recovery codes can sign in and enroll again. Refresh tokens are
/// revoked, and the reason is audited. Your own MFA is turned off with /api/auth/mfa/disable.
/// </summary>
internal class Endpoint(
    IDocumentSession session,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant,
    IConfiguration configuration) : Endpoint<Request, Response>
{
    internal const string SelfResetMessage = "Use your own two-factor settings to turn off MFA.";

    internal const string GlobalRoleMessage =
        "Resetting MFA needs manage_users in a global role, not only through a tenant membership.";

    internal const string PlatformTargetMessage =
        "This user holds a platform role, so only a platform administrator (SuperAdmin) can reset their MFA.";

    public override void Configure()
    {
        Post("/api/users/{userId}/mfa/reset");
        Definition.RequireCapability(SystemCapabilities.ManageUsers, "SuperAdmin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        if (!await PlatformRoles.MayManageUsersAsync(session, User, configuration, ["SuperAdmin"], ct))
        {
            await Send.ResponseAsync(new Response { Message = GlobalRoleMessage }, 403, ct);
            return;
        }

        Guid.TryParse(User.FindFirst("UserId")?.Value, out var actorId);
        if (actorId == req.UserId)
        {
            ThrowError(SelfResetMessage);
            return;
        }

        var user = await session.LoadAsync<User>(req.UserId, ct);
        if (user is null || !await InTenantAsync(user, ct))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (await PlatformRoles.HoldsPlatformRoleAsync(session, configuration, user, ct)
            && !await PlatformRoles.IsSuperAdminAsync(session, User, ct))
        {
            await Send.ResponseAsync(new Response { Message = PlatformTargetMessage }, 403, ct);
            return;
        }

        session.Delete<MfaSecret>(user.Id);

        await RevokeRefreshTokens.ForUserAsync(session, user.Id, "MFA reset by administrator", ct,
            Resolve<barakoCMS.Infrastructure.Services.ISessionEpochService>());

        await AuditLog.RecordAsync(session, tenant.Slug, "auth.mfa.reset", actorId, User.FindFirst("Username")?.Value,
            targetType: "User", targetId: user.Id.ToString(),
            metadata: new() { ["reason"] = req.Reason.Trim() }, ct: ct);

        await session.SaveChangesAsync(ct);

        await Send.ResponseAsync(new Response { Message = "Two-factor authentication was reset." }, cancellation: ct);
    }

    /// <summary>
    /// Users are global documents, so the tenant is checked through membership. The default tenant
    /// and a slug with no registered tenant have no membership model, as in the token issuer.
    /// </summary>
    private async Task<bool> InTenantAsync(User user, CancellationToken ct)
    {
        var slug = tenant.Slug;
        if (tenant.IsDefault) return true;
        if (!await session.Query<Tenant>().AnyAsync(t => t.Slug == slug, ct)) return true;

        return await session.Query<Membership>()
            .AnyAsync(m => m.UserId == user.Id && m.TenantSlug == slug && m.Status == MembershipStatus.Active, ct);
    }
}
