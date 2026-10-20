namespace barakoCMS.Infrastructure.Links;

/// <summary>
/// What the service remembers about one link: that a single use link was redeemed, or that a link was
/// revoked. Keyed by the link's token id. A link nobody redeemed or revoked has no row.
/// </summary>
public class SignedLinkUse
{
    public string Id { get; set; } = string.Empty;
    public bool Redeemed { get; set; }
    public bool Revoked { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
