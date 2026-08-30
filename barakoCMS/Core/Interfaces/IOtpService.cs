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
    /// <returns>
    /// False when the code was stored but could not be emailed. The caller must not report success
    /// in that case: telling somebody a code is on its way when it is not sends them to wait for an
    /// email that will never arrive, and on the device approval path that is indistinguishable from
    /// being locked out.
    /// </returns>
    Task<bool> SendCodeAsync(string email, DeviceContext device, CancellationToken ct);
}
