namespace barakoCMS.Models;

/// <summary>
/// A link that lets someone preview a site while it is held back. One tenant may have many, each
/// with its own label and expiry, and each can be revoked on its own.
/// </summary>
/// <remarks>
/// Only the SHA-256 of the key is stored. The key is shown once, when the link is created, and no
/// response ever returns it or the hash. This is not public settings, so nothing here is delivered.
/// </remarks>
public class SiteShareLink
{
    public Guid Id { get; set; }

    public string Label { get; set; } = string.Empty;

    /// <summary>Lowercase hex SHA-256 of the key.</summary>
    public string KeyHash { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The username of whoever created the link.</summary>
    public string? CreatedBy { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }
}
