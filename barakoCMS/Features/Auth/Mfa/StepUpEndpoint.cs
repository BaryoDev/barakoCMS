using FastEndpoints;
using Marten;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Auth.Mfa;
using barakoCMS.Models;

namespace barakoCMS.Features.Auth.Mfa;

/// <summary>
/// POST /api/auth/mfa/step-up: trade a fresh TOTP or recovery code for a short-lived step-up token.
/// Wrong codes count toward the same lockout as password and MFA login failures.
/// </summary>
internal class StepUpEndpoint(
    IMfaService mfa,
    IDocumentSession session,
    IQuerySession query,
    IConfiguration config,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant,
    barakoCMS.Infrastructure.Auth.AccountLockout lockout) : Endpoint<CodeRequest, StepUpResponse>
{
    public override void Configure()
    {
        Post("/api/auth/mfa/step-up");
        Claims("UserId");
        Options(x => x.RequireRateLimiting("auth"));
    }

    public override async Task HandleAsync(CodeRequest req, CancellationToken ct)
    {
        var device = barakoCMS.Infrastructure.DeviceContext.From(HttpContext);
        if (!Guid.TryParse(User.FindFirst("UserId")?.Value, out var userId))
        {
            ThrowError("Invalid code.");
            return;
        }

        var user = await query.LoadAsync<User>(userId, ct);
        if (user is null)
        {
            ThrowError("Invalid code.");
            return;
        }

        if (!await mfa.IsEnabledAsync(user.Id, ct))
        {
            ThrowError("Set up MFA before using a step-up code.");
            return;
        }

        if (barakoCMS.Infrastructure.Auth.AccountLockout.LockedMessage(user) is { } locked)
        {
            ThrowError(locked);
            return;
        }

        if (!await mfa.VerifyCodeAsync(user.Id, req.Code, ct))
        {
            await lockout.RecordWrongCodeAsync(session, query, tenant.Slug, user, "stepup", "auth.stepup.failed",
                device.IpAddress, ct);

            ThrowError("Invalid code.");
            return;
        }

        barakoCMS.Infrastructure.Auth.AccountLockout.ClearFailures(session, user);

        var (token, expiresAt) = StepUpToken.Create(config, user.Id);
        await AuditLog.RecordAsync(session, tenant.Slug, "auth.stepup.succeeded", user.Id, user.Username,
            ipAddress: device.IpAddress, ct: ct);
        await session.SaveChangesAsync(ct);

        await Send.OkAsync(new StepUpResponse { StepUpToken = token, ExpiresAt = expiresAt }, ct);
    }
}
