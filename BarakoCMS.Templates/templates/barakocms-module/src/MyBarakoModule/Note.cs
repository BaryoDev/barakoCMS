namespace MyBarakoModule;

/// <summary>
/// A document this module owns. Every barakoCMS document is stored per tenant, so a note written
/// in one tenant is never read from another.
/// </summary>
public sealed class Note
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
