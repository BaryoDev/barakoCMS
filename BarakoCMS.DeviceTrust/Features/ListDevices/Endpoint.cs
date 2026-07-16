using FastEndpoints;

namespace BarakoCMS.DeviceTrust.Features.ListDevices;

public sealed record DeviceDto(
    Guid Id,
    string Description,
    string LastSeenIp,
    DateTime LastUsedAt,
    string Status,
    bool Current);

/// <summary>GET /api/devices — the signed-in user's own devices, current one flagged.</summary>
public sealed class Endpoint : EndpointWithoutRequest<List<DeviceDto>>
{
    private readonly IDeviceTrustService _devices;

    public Endpoint(IDeviceTrustService devices) => _devices = devices;

    public override void Configure()
    {
        Get("/api/devices"); // authenticated by default
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        Guid.TryParse(User.FindFirst("UserId")?.Value, out var userId);
        var currentDeviceId = User.FindFirst(DeviceGate.DeviceClaim)?.Value;

        var devices = await _devices.ListAsync(userId, ct);
        var dto = devices.Select(d => new DeviceDto(
            d.Id, d.Description, d.LastSeenIp, d.LastUsedAt, d.Status.ToString(),
            Current: !string.IsNullOrEmpty(currentDeviceId) && d.DeviceId == currentDeviceId)).ToList();

        await SendOkAsync(dto, ct);
    }
}
