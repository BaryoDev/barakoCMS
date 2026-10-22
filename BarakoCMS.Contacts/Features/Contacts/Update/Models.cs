namespace BarakoCMS.Contacts.Features.Contacts.Update;

/// <summary>
/// The whole contact, replacing what is stored. A field left out is cleared, and so is an email
/// left out, which frees the address for another contact.
/// </summary>
internal sealed class UpdateContactRequest : IContactInput
{
    public Guid Id { get; set; }
    public string? Kind { get; set; }
    public string? DisplayName { get; set; }
    public string? GivenName { get; set; }
    public string? FamilyName { get; set; }
    public string? Website { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public Dictionary<string, object>? Fields { get; set; }

    /// <summary>Whether anonymous delivery may serve this contact's Public fields. Off unless sent.</summary>
    public bool IsPublished { get; set; }
}
