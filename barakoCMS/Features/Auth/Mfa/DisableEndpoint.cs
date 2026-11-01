using FastEndpoints;
using Marten;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Auth.Mfa;
using barakoCMS.Models;

namespace barakoCMS.Features.Auth.Mfa;

/// <summary>
/// POST /api/auth/mfa/disable — turn off MFA for the signed-in user. Requires a current TOTP or a
/// recovery code, so a hijacked session (without the second factor) cannot remove it. Wrong codes
/// count toward the same lockout as the other second-factor endpoints.
/// </summary>
internal class DisableEndpoint(
    IMfaService mfa,
    IDocumentSession session,
    IQuerySession query,
    barakoCMS.Core.Interfaces.IEmailService email,
    IConfiguration config,
    ILogger<DisableEndpoint> logger,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant,
    barakoCMS.Infrastructure.Auth.AccountLockout lockout) : Endpoint<CodeRequest, MessageResponse>
{
    public override void Configure()
    {
        Post("/api/auth/mfa/disable");
        Claims("UserId");
        Options(x => x.RequireRateLimiting("auth"));
    }

    public override async Task HandleAsync(CodeRequest req, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirst("UserId")?.Value, out var userId))
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var user = await query.LoadAsync<User>(userId, ct);
        if (user is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        if (barakoCMS.Infrastructure.Auth.AccountLockout.LockedMessage(user) is { } locked)
        {
            ThrowError(locked);
            return;
        }

        var device = barakoCMS.Infrastructure.DeviceContext.From(HttpContext);
        if (!await mfa.DisableAsync(userId, req.Code, ct))
        {
            await lockout.RecordWrongCodeAsync(session, query, tenant.Slug, user, "disable", "auth.mfa.disable.failed",
                device.IpAddress, ct);

            ThrowError("Invalid code. Enter a current authenticator code or a recovery code to turn off MFA.");
            return;
        }

        barakoCMS.Infrastructure.Auth.AccountLockout.ClearFailures(session, user);

        // Sessions opened while the second factor was on must not outlive it.
        await barakoCMS.Infrastructure.Auth.RevokeRefreshTokens.ForUserAsync(session, userId, "mfa_disabled", ct,
            Resolve<barakoCMS.Infrastructure.Services.ISessionEpochService>());

        await AuditLog.RecordAsync(session, tenant.Slug, "auth.mfa.disabled", userId,
            User.FindFirst("Username")?.Value, ipAddress: device.IpAddress, ct: ct);
        await session.SaveChangesAsync(ct);

        await NotifyAsync(user, ct);

        await Send.ResponseAsync(new MessageResponse { Message = "Two-factor authentication is off." });
    }

    /// <summary>
    /// Tells the account owner, out of band, that the second factor was removed. A send failure must
    /// not fail the request and undo the change the user just made.
    /// </summary>
    private async Task NotifyAsync(User user, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(user.Email))
            {
                return;
            }

            var appName = config["Branding:AppName"] ?? "BarakoCMS";
            var body =
                $"<p>Two-factor authentication was just turned off for your {System.Net.WebUtility.HtmlEncode(appName)} account.</p>" +
                "<p>Other devices have been signed out.</p>" +
                "<p><strong>If this wasn't you</strong>, someone else may have access to your account. " +
                "Change your password immediately and contact an administrator.</p>";
            await email.SendEmailAsync(user.Email, $"Two-factor authentication disabled on {appName}", body, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send the MFA-disabled notification for {UserId}", user.Id);
        }
    }
}
