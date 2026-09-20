using FastEndpoints;
using barakoCMS.Infrastructure.Auth.Mfa;

namespace barakoCMS.Features.Auth.Mfa;

/// <summary>GET /api/auth/mfa/status — whether the signed-in user has MFA enabled.</summary>
internal class StatusEndpoint(IMfaService mfa) : EndpointWithoutRequest<StatusResponse>
{
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

        await Send.ResponseAsync(new StatusResponse { Enabled = await mfa.IsEnabledAsync(userId, ct) }, cancellation: ct);
    }
}
