using barakoCMS.Infrastructure;
using Marten;

namespace BarakoCMS.DeviceTrust;

/// <summary>Records and checks trusted devices per user.</summary>
public interface IDeviceTrustService
{
    /// <summary>Upsert the device for the user and mark it Trusted (called after OTP proves it).</summary>
    Task<Device> TrustAsync(Guid userId, DeviceContext ctx, CancellationToken token);

    /// <summary>Is there a Trusted device with this id for this user?</summary>
    Task<bool> IsTrustedAsync(Guid userId, string deviceId, CancellationToken token);

    /// <summary>Update last-seen for an already-known device (best effort).</summary>
    Task TouchAsync(Guid userId, DeviceContext ctx, CancellationToken token);

    Task<IReadOnlyList<Device>> ListAsync(Guid userId, CancellationToken token);

    /// <summary>Revoke one of the user's devices by record id. Returns false if not found.</summary>
    Task<bool> RevokeAsync(Guid userId, Guid deviceRecordId, CancellationToken token);
}

public sealed class DeviceTrustService : IDeviceTrustService
{
    private readonly IDocumentSession _session;

    public DeviceTrustService(IDocumentSession session) => _session = session;

    public async Task<Device> TrustAsync(Guid userId, DeviceContext ctx, CancellationToken token)
    {
        var deviceId = ctx.DeviceId ?? string.Empty;
        var device = string.IsNullOrEmpty(deviceId)
            ? null
            : await _session.Query<Device>().FirstOrDefaultAsync(d => d.UserId == userId && d.DeviceId == deviceId, token);

        var now = DateTime.UtcNow;
        if (device == null)
        {
            device = new Device
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                DeviceId = deviceId,
                UserAgent = ctx.UserAgent,
                Description = ctx.Description,
                FirstSeenIp = ctx.IpAddress,
                LastSeenIp = ctx.IpAddress,
                CreatedAt = now,
            };
        }

        device.Status = DeviceStatus.Trusted;
        device.TrustedAt ??= now;
        device.LastUsedAt = now;
        device.LastSeenIp = ctx.IpAddress;
        device.UserAgent = ctx.UserAgent;
        device.Description = ctx.Description;

        _session.Store(device);
        await _session.SaveChangesAsync(token);
        return device;
    }

    public async Task<bool> IsTrustedAsync(Guid userId, string deviceId, CancellationToken token)
    {
        if (string.IsNullOrEmpty(deviceId))
            return false;
        var device = await _session.Query<Device>()
            .FirstOrDefaultAsync(d => d.UserId == userId && d.DeviceId == deviceId, token);
        return device is { Status: DeviceStatus.Trusted };
    }

    public async Task TouchAsync(Guid userId, DeviceContext ctx, CancellationToken token)
    {
        if (string.IsNullOrEmpty(ctx.DeviceId))
            return;
        var device = await _session.Query<Device>()
            .FirstOrDefaultAsync(d => d.UserId == userId && d.DeviceId == ctx.DeviceId, token);
        if (device == null)
            return;
        device.LastUsedAt = DateTime.UtcNow;
        device.LastSeenIp = ctx.IpAddress;
        _session.Store(device);
        await _session.SaveChangesAsync(token);
    }

    public async Task<IReadOnlyList<Device>> ListAsync(Guid userId, CancellationToken token)
    {
        var devices = await _session.Query<Device>()
            .Where(d => d.UserId == userId && d.Status != DeviceStatus.Revoked)
            .ToListAsync(token);
        return devices.OrderByDescending(d => d.LastUsedAt).ToList();
    }

    public async Task<bool> RevokeAsync(Guid userId, Guid deviceRecordId, CancellationToken token)
    {
        var device = await _session.LoadAsync<Device>(deviceRecordId, token);
        if (device == null || device.UserId != userId)
            return false;
        device.Status = DeviceStatus.Revoked;
        _session.Store(device);
        // Also revoke refresh tokens bound to this device so its sessions can't be refreshed.
        var tokens = await _session.Query<barakoCMS.Models.RefreshToken>()
            .Where(t => t.UserId == userId && t.DeviceId == device.DeviceId && !t.IsRevoked)
            .ToListAsync(token);
        foreach (var t in tokens)
        {
            t.IsRevoked = true;
            t.RevokedReason = "device_revoked";
            t.RevokedAt = DateTime.UtcNow;
            _session.Store(t);
        }
        await _session.SaveChangesAsync(token);
        return true;
    }
}
