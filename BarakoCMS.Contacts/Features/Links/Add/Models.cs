namespace BarakoCMS.Contacts.Features.Links.Add;

internal sealed class AddLinkRequest
{
    /// <summary>The person.</summary>
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    /// <summary>What the person is to the organization, in the tenant's own words.</summary>
    public string? Role { get; set; }
}
