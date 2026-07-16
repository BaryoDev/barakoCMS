using System.Security.Claims;
using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure;
using barakoCMS.Models;
using Microsoft.Extensions.Configuration;

namespace BarakoCMS.DeviceTrust;

/// <summary>
/// Implements the core device gate. OTP sign-in trusts the device; password sign-in is allowed when
/// observe-only, or (with <c>DeviceTrust:Enforce=true</c>) requires a trusted device and otherwise
/// asks the caller to step up to OTP. Adds a <c>did</c> claim so tokens are bound to their device.
/// </summary>
public sealed class DeviceGate : IDeviceGate
{
    public const string DeviceClaim = "did";

    private readonly IDeviceTrustService _devices;
    private readonly bool _enforce;

    public DeviceGate(IDeviceTrustService devices, IConfiguration config)
    {
        _devices = devices;
        _enforce = config.GetValue<bool>("DeviceTrust:Enforce");
    }

    public async Task<IReadOnlyList<Claim>> TrustOnOtpAsync(User user, DeviceContext device, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(device.DeviceId))
            return Array.Empty<Claim>();
        await _devices.TrustAsync(user.Id, device, ct);
        return new[] { new Claim(DeviceClaim, device.DeviceId) };
    }

    public async Task<DeviceGateResult> EvaluatePasswordAsync(User user, DeviceContext device, CancellationToken ct)
    {
        var trusted = !string.IsNullOrEmpty(device.DeviceId)
            && await _devices.IsTrustedAsync(user.Id, device.DeviceId, ct);

        if (trusted)
        {
            await _devices.TouchAsync(user.Id, device, ct);
            return new DeviceGateResult(DeviceDecision.Allow, new[] { new Claim(DeviceClaim, device.DeviceId!) });
        }

        // Unknown device. Observe-only lets it through; enforced mode requires OTP approval.
        return _enforce
            ? new DeviceGateResult(DeviceDecision.ApprovalRequired, Array.Empty<Claim>())
            : DeviceGateResult.Allowed;
    }
}
