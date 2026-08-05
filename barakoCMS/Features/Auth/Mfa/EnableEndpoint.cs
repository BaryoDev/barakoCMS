using FastEndpoints;
using Marten;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Auth.Mfa;

namespace barakoCMS.Features.Auth.Mfa;

/// <summary>
/// POST /api/auth/mfa/enable — confirm a pending enrollment with a code from the authenticator app.
/// On success MFA becomes required at login and one-time recovery codes are returned once.
/// </summary>
public class EnableEndpoint : Endpoint<CodeRequest, EnableResponse>
{
    private readonly IMfaService _mfa;
    private readonly IDocumentSession _session;
    private readonly barakoCMS.Infrastructure.Multitenancy.TenantContext _tenant;

    public EnableEndpoint(IMfaService mfa, IDocumentSession session, barakoCMS.Infrastructure.Multitenancy.TenantContext tenant)
    {
        _mfa = mfa;
        _session = session;
        _tenant = tenant;
    }

    public override void Configure()
    {
        Post("/api/auth/mfa/enable");
        Claims("UserId");
    }

    public override async Task HandleAsync(CodeRequest req, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirst("UserId")?.Value, out var userId))
        {
            await SendUnauthorizedAsync(ct);
            return;
        }

        var recoveryCodes = await _mfa.ConfirmSetupAsync(userId, req.Code, ct);
        if (recoveryCodes is null)
        {
            ThrowError("Invalid code. Make sure you scanned the latest secret and try again.");
            return;
        }

        await AuditLog.RecordAsync(_session, _tenant.Slug, "auth.mfa.enabled", userId,
            User.FindFirst("Username")?.Value, ct: ct);
        await _session.SaveChangesAsync(ct);

        await SendAsync(new EnableResponse
        {
            Message = "Two-factor authentication is on. Save your recovery codes somewhere safe.",
            RecoveryCodes = recoveryCodes.ToList(),
        });
    }
}
