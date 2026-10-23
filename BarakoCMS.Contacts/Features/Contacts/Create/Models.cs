namespace BarakoCMS.Contacts.Features.Contacts.Create;

internal sealed class CreateContactRequest : IContactInput
{
    /// <summary>Person or Organization.</summary>
    public string? Kind { get; set; }
    public string? DisplayName { get; set; }
    public string? GivenName { get; set; }
    public string? FamilyName { get; set; }
    public string? Website { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }

    /// <summary>Custom fields declared on the contact-record content type.</summary>
    public Dictionary<string, object>? Fields { get; set; }
}
