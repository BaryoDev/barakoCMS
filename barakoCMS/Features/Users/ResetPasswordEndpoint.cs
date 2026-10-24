using FastEndpoints;
using Marten;
using barakoCMS.Models;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Services;

namespace barakoCMS.Features.Users.ResetPassword;

internal class ResetPasswordRequest
{
    /// <summary>Bound from the {userId} route segment.</summary>
    public Guid UserId { get; set; }
    public string NewPassword { get; set; } = string.Empty;
}

/// <summary>
/// POST /api/users/{userId}/password: an administrator holding manage_users sets another user's
/// password (recovery or rotation). Enforces the password policy and revokes the user's refresh
/// tokens; no current-password check, since this is an administrative reset. Outstanding
/// short-lived access tokens expire on their own rather than being individually revoked.
/// </summary>
[barakoCMS.Infrastructure.Filters.NoIdempotentReplay]
internal class ResetPasswordEndpoint(
    IDocumentSession session,
    IPasswordPolicyValidator passwordValidator,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant,
    IConfiguration configuration) : Endpoint<ResetPasswordRequest>
{
    internal const string SelfResetMessage = "Use your own password settings to change your password.";

    internal const string GlobalRoleMessage =
        "Resetting a password needs manage_users in a global role, not only through a tenant membership.";

    internal const string PlatformTargetMessage =
        "This user holds a platform role, so only a platform administrator (SuperAdmin) can reset their password.";

    public override void Configure()
    {
        Post("/api/users/{userId}/password");
        Definition.RequireCapability(SystemCapabilities.ManageUsers, "SuperAdmin");
    }

    public override async Task HandleAsync(ResetPasswordRequest req, CancellationToken ct)
    {
        if (!await PlatformRoles.MayManageUsersAsync(session, User, configuration, ["SuperAdmin"], ct))
        {
            AddError(GlobalRoleMessage);
            await Send.ErrorsAsync(403, ct);
            return;
        }

        Guid.TryParse(User.FindFirst("UserId")?.Value, out var actorId);
        if (actorId == req.UserId)
        {
            ThrowError(SelfResetMessage);
            return;
        }

        var user = await session.LoadAsync<User>(req.UserId, ct);
        if (user is null || !await PlatformRoles.InTenantAsync(session, tenant, user, ct))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (await PlatformRoles.HoldsPlatformRoleAsync(session, configuration, user, ct)
            && !await PlatformRoles.IsSuperAdminAsync(session, User, ct))
        {
            AddError(PlatformTargetMessage);
            await Send.ErrorsAsync(403, ct);
            return;
        }

        var (isValid, errorMessage) = passwordValidator.Validate(req.NewPassword);
        if (!isValid)
        {
            ThrowError(r => r.NewPassword, errorMessage!);
            return;
        }

        user.PasswordHash = barakoCMS.Infrastructure.Auth.PasswordHashing.Hash(req.NewPassword);
        session.Store(user);

        // Revoke the user's refresh tokens so existing sessions can't be refreshed after the reset.
        await RevokeRefreshTokens.ForUserAsync(session, user.Id, "Password reset by administrator", ct, Resolve<barakoCMS.Infrastructure.Services.ISessionEpochService>());

        await AuditLog.RecordAsync(session, tenant.Slug, "auth.password.reset", actorId, User.FindFirst("Username")?.Value,
            targetType: "User", targetId: user.Id.ToString(), ct: ct);

        await session.SaveChangesAsync(ct);

        await Send.OkAsync(ct);
    }
}
