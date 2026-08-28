using FastEndpoints;
using barakoCMS.Infrastructure.Auth.Mfa;

namespace barakoCMS.Features.Auth.Mfa;

/// <summary>GET /api/auth/mfa/status — whether the signed-in user has MFA enabled.</summary>
public class StatusEndpoint : EndpointWithoutRequest<StatusResponse>
{
    private readonly IMfaService _mfa;

    public StatusEndpoint(IMfaService mfa) => _mfa = mfa;

    public override void Configure()
    {
        Get("/api/auth/mfa/status");
        Claims("UserId");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirst("UserId")?.Value, out var userId))
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        await Send.ResponseAsync(new StatusResponse { Enabled = await _mfa.IsEnabledAsync(userId, ct) }, cancellation: ct);
    }
}
