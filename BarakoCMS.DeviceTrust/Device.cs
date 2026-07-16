namespace BarakoCMS.DeviceTrust;

/// <summary>A client device (browser/app) recognized for a user, keyed by its client-supplied id.</summary>
public class Device
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }

    /// <summary>The client-generated device id sent as the <c>X-Device-Id</c> header.</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>Optional friendly name the user can set later.</summary>
    public string? Label { get; set; }

    public string UserAgent { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty; // e.g. "Chrome on macOS"
    public string FirstSeenIp { get; set; } = string.Empty;
    public string LastSeenIp { get; set; } = string.Empty;

    public DeviceStatus Status { get; set; } = DeviceStatus.Trusted;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? TrustedAt { get; set; }
    public DateTime LastUsedAt { get; set; } = DateTime.UtcNow;
}

public enum DeviceStatus
{
    Pending, // seen but not yet approved
    Trusted, // approved (via OTP)
    Revoked, // access removed
}
