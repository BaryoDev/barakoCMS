using FastEndpoints;
using Marten;
using barakoCMS.Infrastructure.Auth.Mfa;

namespace barakoCMS.Features.Auth.Mfa;

/// <summary>
/// POST /api/auth/mfa/setup — begin TOTP enrollment for the signed-in user. Returns a fresh secret and
/// otpauth URI to display once. Enrollment is not active until confirmed via /enable, so calling this
/// again before enabling simply replaces the pending secret. 409 if MFA is already enabled.
/// </summary>
internal class SetupEndpoint : EndpointWithoutRequest<SetupResponse>
{
    private readonly IMfaService _mfa;
    private readonly IQuerySession _session;

    public SetupEndpoint(IMfaService mfa, IQuerySession session)
    {
        _mfa = mfa;
        _session = session;
    }

    public override void Configure()
    {
        Post("/api/auth/mfa/setup");
        Claims("UserId");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirst("UserId")?.Value, out var userId))
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        if (await _mfa.IsEnabledAsync(userId, ct))
        {
            await Send.ResponseAsync(new SetupResponse(), 409, ct);
            return;
        }

        var user = await _session.LoadAsync<barakoCMS.Models.User>(userId, ct);
        if (user is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var (secret, uri) = await _mfa.BeginSetupAsync(user, ct);
        await Send.ResponseAsync(new SetupResponse { Secret = secret, OtpauthUri = uri }, cancellation: ct);
    }
}
