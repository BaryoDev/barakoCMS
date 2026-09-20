using FastEndpoints;
using Marten;
using barakoCMS.Models;
using barakoCMS.Infrastructure.Auth;
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
internal class ResetPasswordEndpoint(
    IDocumentSession session,
    IPasswordPolicyValidator passwordValidator) : Endpoint<ResetPasswordRequest>
{
    public override void Configure()
    {
        Post("/api/users/{userId}/password");
        Definition.RequireCapability(SystemCapabilities.ManageUsers, "SuperAdmin");
    }

    public override async Task HandleAsync(ResetPasswordRequest req, CancellationToken ct)
    {
        var user = await session.LoadAsync<User>(req.UserId, ct);
        if (user is null)
        {
            await Send.NotFoundAsync(ct);
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

        await session.SaveChangesAsync(ct);

        await Send.OkAsync(ct);
    }
}
