using FastEndpoints;

namespace BarakoCMS.DeviceTrust.Features.RevokeDevice;

public sealed class Request
{
    public Guid Id { get; set; }
}

/// <summary>POST /api/devices/{id}/revoke — revoke one of the signed-in user's own devices.</summary>
public sealed class Endpoint : Endpoint<Request>
{
    private readonly IDeviceTrustService _devices;

    public Endpoint(IDeviceTrustService devices) => _devices = devices;

    public override void Configure()
    {
        Post("/api/devices/{id}/revoke"); // authenticated by default
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        Guid.TryParse(User.FindFirst("UserId")?.Value, out var userId);
        var revoked = await _devices.RevokeAsync(userId, req.Id, ct);
        if (!revoked)
        {
            await SendNotFoundAsync(ct);
            return;
        }
        await SendOkAsync(ct);
    }
}
