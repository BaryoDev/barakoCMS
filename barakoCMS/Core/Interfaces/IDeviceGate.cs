using System.Security.Claims;
using barakoCMS.Infrastructure;
using barakoCMS.Models;

namespace barakoCMS.Core.Interfaces;

/// <summary>Whether a password sign-in may proceed from the requesting device.</summary>
public enum DeviceDecision
{
    Allow,            // device is trusted (or trust is not enforced) — issue tokens
    ApprovalRequired, // unknown device — send an OTP to approve it before issuing tokens
}

/// <summary>Outcome of a password-login device check, plus any claims to bake into the token.</summary>
public sealed record DeviceGateResult(DeviceDecision Decision, IReadOnlyList<Claim> Claims)
{
    public static readonly DeviceGateResult Allowed = new(DeviceDecision.Allow, Array.Empty<Claim>());
}

/// <summary>
/// The seam the DeviceTrust module plugs into. Core auth calls it; the default implementation is a
/// no-op so device trust is entirely opt-in. OTP sign-in trusts the device (the code proves it);
/// password sign-in on an unknown device is asked to step up to OTP.
/// </summary>
public interface IDeviceGate
{
    /// <summary>OTP verified: record/trust this device for the user and return claims to add (e.g. the device id).</summary>
    Task<IReadOnlyList<Claim>> TrustOnOtpAsync(User user, DeviceContext device, CancellationToken ct);

    /// <summary>Password verified: is this a trusted device? If not, the caller sends an approval OTP instead of tokens.</summary>
    Task<DeviceGateResult> EvaluatePasswordAsync(User user, DeviceContext device, CancellationToken ct);
}

/// <summary>Default gate: no device tracking. OTP adds no claims; password always allowed.</summary>
public sealed class NoopDeviceGate : IDeviceGate
{
    public Task<IReadOnlyList<Claim>> TrustOnOtpAsync(User user, DeviceContext device, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<Claim>>(Array.Empty<Claim>());

    public Task<DeviceGateResult> EvaluatePasswordAsync(User user, DeviceContext device, CancellationToken ct)
        => Task.FromResult(DeviceGateResult.Allowed);
}
