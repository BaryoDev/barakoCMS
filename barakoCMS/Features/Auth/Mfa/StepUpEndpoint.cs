using FastEndpoints;
using Marten;
using Marten.Patching;
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

        if (user.LockoutUntil is { } until && until > DateTime.UtcNow)
        {
            var remaining = (int)(until - DateTime.UtcNow).TotalMinutes + 1;
            ThrowError($"Account is locked due to multiple failed attempts. Please try again in {remaining} minute(s).");
            return;
        }

        if (!await mfa.VerifyCodeAsync(user.Id, req.Code, ct))
        {
            session.Patch<User>(user.Id).Increment(x => x.FailedLoginAttempts);
            await session.SaveChangesAsync(ct);

            var attempts = (await query.LoadAsync<User>(user.Id, ct))?.FailedLoginAttempts ?? 0;
            if (attempts >= barakoCMS.Infrastructure.Auth.AccountLockout.MaxFailedAttempts
                && await lockout.TryLockAsync(user, ct))
            {
                await AuditLog.RecordAsync(session, tenant.Slug, "auth.account.locked", user.Id, user.Username,
                    metadata: new() { ["attempts"] = attempts, ["factor"] = "stepup" }, ipAddress: device.IpAddress, ct: ct);
            }
            await AuditLog.RecordAsync(session, tenant.Slug, "auth.stepup.failed", user.Id, user.Username,
                metadata: new() { ["attempts"] = attempts }, ipAddress: device.IpAddress, ct: ct);
            await session.SaveChangesAsync(ct);

            ThrowError("Invalid code.");
            return;
        }

        if (user.FailedLoginAttempts > 0)
            session.Patch<User>(user.Id).Set(x => x.FailedLoginAttempts, 0);

        var (token, expiresAt) = StepUpToken.Create(config, user.Id);
        await AuditLog.RecordAsync(session, tenant.Slug, "auth.stepup.succeeded", user.Id, user.Username,
            ipAddress: device.IpAddress, ct: ct);
        await session.SaveChangesAsync(ct);

        await Send.OkAsync(new StepUpResponse { StepUpToken = token, ExpiresAt = expiresAt }, ct);
    }
}
