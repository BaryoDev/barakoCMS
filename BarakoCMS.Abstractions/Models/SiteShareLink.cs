namespace barakoCMS.Models;

/// <summary>
/// A link that lets someone see what is not public yet: the whole site while it is held back, or
/// one entry whatever its status. One tenant may have many, each with its own label and expiry, and
/// each can be revoked on its own.
/// </summary>
/// <remarks>
/// Only the SHA-256 of the key is stored. The key is shown once, when the link is created, and no
/// response ever returns it or the hash. This is not public settings, so nothing here is delivered.
/// </remarks>
public class SiteShareLink
{
    public Guid Id { get; set; }

    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// Lowercase hex SHA-256 of the key, for a link to the site. A link that names an entry is
    /// stored under the SHA-256 of one 0xFF byte followed by the key, which no lookup by the plain
    /// hash can reach, so a build from before entry links never takes one for a link to the site.
    /// </summary>
    public string KeyHash { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The username of whoever created the link.</summary>
    public string? CreatedBy { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>The one entry this link opens. Null means the link is for the whole held site.</summary>
    /// <remarks>
    /// A link stored before entry links existed has no such field and reads back as null, which is
    /// the meaning it always had.
    /// </remarks>
    public Guid? EntryId { get; set; }

    /// <summary>
    /// For a page link, the path the page is served at, as whoever created the link gave it. Only
    /// ever set together with <see cref="EntryId"/>, and never used to look anything up.
    /// </summary>
    public string? Path { get; set; }

    /// <summary>
    /// True when <c>POST /api/preview</c> issued the link. Only such a link is accepted in the
    /// <c>?preview=</c> query of a slug read, because its key lives 30 minutes and a query string
    /// ends up in access logs. It is accepted nowhere else, and is not listed with the entry's links.
    /// </summary>
    public bool Preview { get; set; }
}
