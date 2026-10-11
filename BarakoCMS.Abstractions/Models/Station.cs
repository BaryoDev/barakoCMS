namespace barakoCMS.Models;

/// <summary>
/// A machine with no person signed in, such as a kiosk or a TV board. Holds a fixed list of
/// capabilities in one tenant. Only the hash of its key is stored.
/// </summary>
public sealed class Station
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string TenantSlug { get; set; } = string.Empty;
    public string KeyHash { get; set; } = string.Empty;

    /// <summary>The start of the key, enough to tell stations apart in a list and never enough to use.</summary>
    public string DisplayPrefix { get; set; } = string.Empty;

    public List<string> Capabilities { get; set; } = new();
    public Guid CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool Revoked { get; set; }
    public DateTime? RevokedAt { get; set; }
}
