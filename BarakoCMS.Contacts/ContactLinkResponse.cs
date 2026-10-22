namespace BarakoCMS.Contacts;

internal sealed class ContactLinkResponse
{
    public Guid PersonId { get; set; }
    public Guid OrganizationId { get; set; }
    public string? Role { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public static ContactLinkResponse From(ContactLink link) => new()
    {
        PersonId = link.PersonId,
        OrganizationId = link.OrganizationId,
        Role = link.Role,
        CreatedAt = link.CreatedAt,
    };
}
