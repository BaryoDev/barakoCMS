using barakoCMS.Models;
using Marten;

namespace BarakoCMS.Contacts;

/// <summary>
/// The <c>contact-record</c> content type: the fields every contact has, which of them are
/// Sensitive, and a place for a tenant to add its own.
/// </summary>
/// <remarks>
/// A contact is not stored as content, because email needs a unique index and content has none. The
/// content type is the contract all the same: it validates the fields, and its per-field sensitivity
/// is what public delivery filters by. A tenant adds fields to its own copy with the content type
/// endpoints. A tenant that has no copy is served the one below.
/// </remarks>
internal static class ContactRecord
{
    public const string TypeName = "contact-record";

    public const string DisplayName = "displayName";
    public const string GivenName = "givenName";
    public const string FamilyName = "familyName";
    public const string Kind = "kind";
    public const string Website = "website";
    public const string Email = "email";
    public const string Phone = "phone";
    public const string Address = "address";

    private static readonly string[] StandardNames =
        [DisplayName, GivenName, FamilyName, Kind, Website, Email, Phone, Address];

    public static ContentTypeDefinition Definition() => new()
    {
        Id = Guid.Parse("00000000-0000-0000-0000-0000C0A7AC01"),
        Name = TypeName,
        DisplayName = "Contact",
        Description = "A person or an organization. Add your own fields here.",
        IsPubliclyDeliverable = false,
        Fields =
        [
            new() { Name = DisplayName, Type = "string", Sensitivity = SensitivityLevel.Public },
            new() { Name = GivenName, Type = "string", Sensitivity = SensitivityLevel.Public },
            new() { Name = FamilyName, Type = "string", Sensitivity = SensitivityLevel.Public },
            new() { Name = Kind, Type = "string", Sensitivity = SensitivityLevel.Public },
            new() { Name = Website, Type = "url", Sensitivity = SensitivityLevel.Public },
            new() { Name = Email, Type = "email", Sensitivity = SensitivityLevel.Sensitive },
            new() { Name = Phone, Type = "string", Sensitivity = SensitivityLevel.Sensitive },
            new() { Name = Address, Type = "string", Sensitivity = SensitivityLevel.Sensitive },
        ],
    };

    public static bool IsStandardName(string name) =>
        StandardNames.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>The tenant's own copy of the type, else the built-in one.</summary>
    public static async Task<ContentTypeDefinition> SchemaAsync(IQuerySession session, CancellationToken ct) =>
        await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name == TypeName, ct)
        ?? Definition();

    /// <summary>All the contact's fields as one data bag, for validation.</summary>
    public static Dictionary<string, object> Bag(Contact contact)
    {
        var bag = new Dictionary<string, object>(contact.Fields) { [DisplayName] = contact.DisplayName, [Kind] = contact.Kind.ToString() };
        Put(bag, GivenName, contact.GivenName);
        Put(bag, FamilyName, contact.FamilyName);
        Put(bag, Website, contact.Website);
        Put(bag, Email, contact.Email);
        Put(bag, Phone, contact.Phone);
        Put(bag, Address, contact.Address);
        return bag;
    }

    /// <summary>
    /// What anonymous delivery may start from. Email, phone and address are not in it at all, so a
    /// type that marks one of them Public by mistake still cannot serve it.
    /// </summary>
    public static Content ForDelivery(Contact contact)
    {
        var bag = Bag(contact);
        bag.Remove(Email);
        bag.Remove(Phone);
        bag.Remove(Address);

        return new Content
        {
            Id = contact.Id,
            ContentType = TypeName,
            Data = bag,
            Status = ContentStatus.Published,
            Sensitivity = SensitivityLevel.Public,
            CreatedAt = contact.CreatedAt.UtcDateTime,
            UpdatedAt = contact.UpdatedAt.UtcDateTime,
        };
    }

    private static void Put(Dictionary<string, object> bag, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            bag[name] = value;
        }
    }
}
