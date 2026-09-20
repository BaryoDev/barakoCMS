using FastEndpoints;
using Marten;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Auth.Mfa;

namespace barakoCMS.Features.Auth.Mfa;

/// <summary>
/// POST /api/auth/mfa/enable — confirm a pending enrollment with a code from the authenticator app.
/// On success MFA becomes required at login and one-time recovery codes are returned once.
/// </summary>
internal class EnableEndpoint(
    IMfaService mfa,
    IDocumentSession session,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant,
    barakoCMS.Core.Interfaces.IEmailService email,
    IConfiguration config,
    ILogger<EnableEndpoint> logger) : Endpoint<CodeRequest, EnableResponse>
{
    public override void Configure()
    {
        Post("/api/auth/mfa/enable");
        Claims("UserId");
    }

    public override async Task HandleAsync(CodeRequest req, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirst("UserId")?.Value, out var userId))
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var recoveryCodes = await mfa.ConfirmSetupAsync(userId, req.Code, ct);
        if (recoveryCodes is null)
        {
            ThrowError("Invalid code. Make sure you scanned the latest secret and try again.");
            return;
        }

        // Sessions opened before MFA existed must not outlive it. Otherwise an attacker who hijacked a
        // session on an unprotected account can enrol their own authenticator and keep the account:
        // the enrolment is silent, and their existing session survives it.
        await barakoCMS.Infrastructure.Auth.RevokeRefreshTokens.ForUserAsync(session, userId, "mfa_enabled", ct, Resolve<barakoCMS.Infrastructure.Services.ISessionEpochService>());

        await AuditLog.RecordAsync(session, tenant.Slug, "auth.mfa.enabled", userId,
            User.FindFirst("Username")?.Value, ct: ct);
        await session.SaveChangesAsync(ct);

        await NotifyAsync(userId, ct);

        await Send.ResponseAsync(new EnableResponse
        {
            Message = "Two-factor authentication is on. Save your recovery codes somewhere safe. " +
                      "Other devices have been signed out.",
            RecoveryCodes = recoveryCodes.ToList(),
        });
    }

    /// <summary>
    /// Tells the account owner, out of band, that a second factor was added. If someone else enrolled
    /// it, this is the only signal they get — so a send failure must not fail the request and undo the
    /// enrolment the user just asked for.
    /// </summary>
    private async Task NotifyAsync(Guid userId, CancellationToken ct)
    {
        try
        {
            var user = await session.LoadAsync<barakoCMS.Models.User>(userId, ct);
            if (user is null || string.IsNullOrWhiteSpace(user.Email)) return;

            var appName = config["Branding:AppName"] ?? "BarakoCMS";
            var body =
                $"<p>Two-factor authentication was just turned on for your {appName} account.</p>" +
                "<p>Other devices have been signed out, so you will be asked to sign in again with a code.</p>" +
                "<p><strong>If this wasn't you</strong>, someone else may have access to your account. " +
                "Change your password immediately and contact an administrator.</p>";
            await email.SendEmailAsync(user.Email, $"Two-factor authentication enabled on {appName}", body, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send the MFA-enabled notification for {UserId}", userId);
        }
    }
}
