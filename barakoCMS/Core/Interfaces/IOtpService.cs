using barakoCMS.Infrastructure;

namespace barakoCMS.Core.Interfaces;

/// <summary>
/// Issues and emails one-time sign-in codes. Shared by the OTP request endpoint and by password
/// login when it needs to step up to approve a new device.
/// </summary>
public interface IOtpService
{
    /// <summary>
    /// Invalidates any outstanding codes for <paramref name="email"/>, stores a fresh hashed 6-digit
    /// code, and emails it with the requesting device's context (Maya-style "DO NOT SHARE" notice).
    /// The caller is responsible for confirming the email belongs to a real user first.
    /// </summary>
    Task SendCodeAsync(string email, DeviceContext device, CancellationToken ct);
}
