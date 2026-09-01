using FastEndpoints;
using FluentValidation;
using Marten;
using barakoCMS.Models;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Services;

namespace barakoCMS.Features.Me;

internal class ChangePasswordRequest
{
    public string CurrentPassword { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
}

internal class ChangePasswordResponse
{
    public string Message { get; set; } = string.Empty;
}

internal class ChangePasswordValidator : Validator<ChangePasswordRequest>
{
    public ChangePasswordValidator()
    {
        RuleFor(x => x.CurrentPassword).NotEmpty();
        // Mirrors PasswordPolicyValidator's minimum; the policy validator enforces the full rules.
        RuleFor(x => x.NewPassword).NotEmpty().MinimumLength(12);
    }
}

/// <summary>
/// POST /api/me/password — the signed-in user changes their own password. It re-verifies the current
/// password first (so an idle-but-stolen session can't silently take over the account), enforces the
/// password policy, stores a fresh BCrypt hash, and revokes the user's refresh tokens. Existing
/// short-lived access tokens are not individually killed; they expire on their own. It lives under the
/// global <c>/api/me</c> identity prefix, so it is not tenant-scoped — a user is global.
/// </summary>
internal class ChangePasswordEndpoint : Endpoint<ChangePasswordRequest, ChangePasswordResponse>
{
    private readonly IDocumentSession _session;
    private readonly IPasswordPolicyValidator _passwordValidator;

    public ChangePasswordEndpoint(IDocumentSession session, IPasswordPolicyValidator passwordValidator)
    {
        _session = session;
        _passwordValidator = passwordValidator;
    }

    public override void Configure()
    {
        Post("/api/me/password"); // authenticated by default
        // Covered by the global per-IP limiter. Grinding the current-password check is already
        // infeasible (authenticated-only, BCrypt over a 12+ char complexity policy).
    }

    public override async Task HandleAsync(ChangePasswordRequest req, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirst("UserId")?.Value, out var userId))
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var user = await _session.LoadAsync<User>(userId, ct);
        if (user is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        // Re-authenticate with the current password before allowing a change.
        if (!BCrypt.Net.BCrypt.Verify(req.CurrentPassword, user.PasswordHash))
        {
            ThrowError(r => r.CurrentPassword, "Current password is incorrect.");
            return;
        }

        var (isValid, errorMessage) = _passwordValidator.Validate(req.NewPassword);
        if (!isValid)
        {
            ThrowError(r => r.NewPassword, errorMessage!);
            return;
        }

        // A "change" that keeps the same password is almost certainly a mistake.
        if (BCrypt.Net.BCrypt.Verify(req.NewPassword, user.PasswordHash))
        {
            ThrowError(r => r.NewPassword, "New password must be different from the current password.");
            return;
        }

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.NewPassword);
        _session.Store(user);

        // Revoke the user's refresh tokens so a token stolen before the change can't be refreshed
        // afterwards. (Outstanding short-lived access tokens still expire on their own.)
        await RevokeRefreshTokens.ForUserAsync(_session, user.Id, "Password changed", ct, Resolve<barakoCMS.Infrastructure.Services.ISessionEpochService>());

        await _session.SaveChangesAsync(ct);

        await Send.ResponseAsync(new ChangePasswordResponse { Message = "Password changed." });
    }
}
