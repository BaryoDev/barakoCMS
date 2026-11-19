namespace barakoCMS.Models;

/// <summary>
/// Represents a refresh token for JWT token rotation.
/// Refresh tokens have longer expiry (7 days) and are rotated on each use.
/// </summary>
public class RefreshToken
{
    public Guid Id { get; set; }
    
    /// <summary>
    /// The token in plain text. Only rows written before tokens were hashed have it; new rows leave
    /// it null and set <see cref="TokenHash"/>. Kept so those rows still refresh once, which
    /// replaces them with a hashed row.
    /// </summary>
    public string? Token { get; set; }

    /// <summary>
    /// SHA-256 of the token, from <see cref="HashOf"/>. What a presented token is looked up by, so
    /// a copy of this table does not hold working sessions.
    /// </summary>
    public string? TokenHash { get; set; }

    /// <summary>The value stored in <see cref="TokenHash"/> for a token: SHA-256, lowercase hex.</summary>
    public static string HashOf(string token) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
    
    /// <summary>A new unrevoked refresh token row for a raw token, stored by its hash.</summary>
    internal static RefreshToken Issue(Guid userId, string rawToken, DateTime expiresAt, string? deviceId) => new()
    {
        Id = Guid.NewGuid(),
        TokenHash = HashOf(rawToken),
        UserId = userId,
        ExpiresAt = expiresAt,
        CreatedAt = DateTime.UtcNow,
        IsRevoked = false,
        DeviceId = CapDeviceId(deviceId),
    };

    /// <summary>Longest stored <see cref="DeviceId"/>. A client chooses it, so it is cut on write.</summary>
    public const int MaxDeviceIdLength = 128;

    internal static string? CapDeviceId(string? deviceId) =>
        deviceId is { Length: > MaxDeviceIdLength } ? deviceId[..MaxDeviceIdLength] : deviceId;

    /// <summary>
    /// The first row of a new session. <paramref name="method"/> is how the person signed in:
    /// <c>password</c>, <c>code</c>, <c>link</c> or <c>social:&lt;provider&gt;</c>, with <c>+mfa</c> after a
    /// second factor. <paramref name="sessionId"/> is the id already put in the access token's
    /// <c>sid</c> claim.
    /// </summary>
    public static RefreshToken StartSession(
        Guid userId, string rawToken, DateTime expiresAt, Infrastructure.DeviceContext device, string method, Guid sessionId)
    {
        var now = DateTime.UtcNow;
        var row = Issue(userId, rawToken, expiresAt, device.DeviceId);
        row.SessionId = sessionId;
        row.SessionStartedAt = now;
        row.Device = Infrastructure.DeviceContext.StoredDescription(device.UserAgent);
        row.SignInIp = device.IpAddress;
        row.SignInMethod = method;
        row.LastSeenAt = now;
        row.LastSeenIp = device.IpAddress;
        return row;
    }

    /// <summary>
    /// The row that replaces this one on refresh. The session id, start, sign-in address and method
    /// carry over; the last seen time and address move to this request. A row from before sessions
    /// existed has no id, so the new row starts one.
    /// </summary>
    public RefreshToken Successor(string rawToken, DateTime expiresAt, Infrastructure.DeviceContext device, Guid sessionId)
    {
        var now = DateTime.UtcNow;
        var row = Issue(UserId, rawToken, expiresAt, DeviceId);
        row.SessionId = sessionId;
        row.SessionStartedAt = SessionStartedAt ?? now;
        row.Device = string.IsNullOrEmpty(Device) ? Infrastructure.DeviceContext.StoredDescription(device.UserAgent) : Device;
        row.SignInIp = string.IsNullOrEmpty(SignInIp) ? device.IpAddress : SignInIp;
        row.SignInMethod = SignInMethod;
        row.LastSeenAt = now;
        row.LastSeenIp = device.IpAddress;
        return row;
    }

    /// <summary>Drops what identifies the person from a row that is no longer the session's live one.</summary>
    public void ClearIdentifiers()
    {
        Device = null;
        SignInIp = null;
        LastSeenIp = null;
    }

    /// <summary>
    /// The user this refresh token belongs to
    /// </summary>
    public Guid UserId { get; set; }
    
    /// <summary>
    /// When this refresh token expires
    /// </summary>
    public DateTime ExpiresAt { get; set; }
    
    /// <summary>
    /// When this refresh token was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// Whether this token has been revoked (used or invalidated)
    /// </summary>
    public bool IsRevoked { get; set; }
    
    /// <summary>
    /// Reason for revocation: "used", "logout", "password_change", "admin_revoke"
    /// </summary>
    public string? RevokedReason { get; set; }
    
    /// <summary>
    /// When this token was revoked (if applicable)
    /// </summary>
    public DateTime? RevokedAt { get; set; }

    /// <summary>
    /// The client device id (X-Device-Id) this token is bound to, when device trust is enabled.
    /// Lets a refresh be tied to its device and revoked with it.
    /// </summary>
    public string? DeviceId { get; set; }

    /// <summary>
    /// The session this row belongs to, the <c>sid</c> claim of its access tokens. Null on a row
    /// from before sessions; its next refresh gives the new row one.
    /// </summary>
    public Guid? SessionId { get; set; }

    /// <summary>When the session began, carried across rotation.</summary>
    public DateTime? SessionStartedAt { get; set; }

    /// <summary>"Browser on OS" for the session, at most 80 characters. Cleared on a used row.</summary>
    public string? Device { get; set; }

    /// <summary>The address the session was started from. Cleared on a used row.</summary>
    public string? SignInIp { get; set; }

    /// <summary><c>password</c>, <c>code</c>, <c>link</c> or <c>social:&lt;provider&gt;</c>, plus <c>+mfa</c>.</summary>
    public string? SignInMethod { get; set; }

    /// <summary>When this session last refreshed.</summary>
    public DateTime? LastSeenAt { get; set; }

    /// <summary>The address of the last refresh. Cleared on a used row.</summary>
    public string? LastSeenIp { get; set; }
}
