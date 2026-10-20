namespace barakoCMS.Models;

/// <summary>
/// A content type owned by an account and used by every tenant in it. A global document (not
/// tenant-scoped).
/// </summary>
/// <remarks>
/// Entries of the type stay in the tenant that wrote them and are validated against
/// <see cref="Definition"/>. The definition cannot live in one tenant's partition, because the other
/// tenants of the account could not read it, and copying it into each partition is the drift this
/// exists to remove. A tenant's own <see cref="ContentTypeDefinition"/> of the same name is refused
/// on create, and where one already exists the account's definition wins.
/// </remarks>
public class AccountContentType
{
    public Guid Id { get; set; }

    public Guid AccountId { get; set; }

    /// <summary>The definition's normalised name, kept at the top so it can carry the unique index.</summary>
    public string Name { get; set; } = string.Empty;

    public ContentTypeDefinition Definition { get; set; } = new();

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
