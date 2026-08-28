using FastEndpoints;
using Marten;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Auth.Mfa;

namespace barakoCMS.Features.Auth.Mfa;

/// <summary>
/// POST /api/auth/mfa/disable — turn off MFA for the signed-in user. Requires a current TOTP or a
/// recovery code, so a hijacked session (without the second factor) cannot remove it.
/// </summary>
public class DisableEndpoint : Endpoint<CodeRequest, MessageResponse>
{
    private readonly IMfaService _mfa;
    private readonly IDocumentSession _session;
    private readonly barakoCMS.Infrastructure.Multitenancy.TenantContext _tenant;

    public DisableEndpoint(IMfaService mfa, IDocumentSession session, barakoCMS.Infrastructure.Multitenancy.TenantContext tenant)
    {
        _mfa = mfa;
        _session = session;
        _tenant = tenant;
    }

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

        if (!await _mfa.DisableAsync(userId, req.Code, ct))
        {
            ThrowError("Invalid code. Enter a current authenticator code or a recovery code to turn off MFA.");
            return;
        }

        await AuditLog.RecordAsync(_session, _tenant.Slug, "auth.mfa.disabled", userId,
            User.FindFirst("Username")?.Value, ct: ct);
        await _session.SaveChangesAsync(ct);

        await Send.ResponseAsync(new MessageResponse { Message = "Two-factor authentication is off." });
    }
}
