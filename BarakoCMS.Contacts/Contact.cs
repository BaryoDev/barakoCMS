using System.Security.Cryptography;
using System.Text;

namespace BarakoCMS.Contacts;

/// <summary>What a contact is. The numbers are stored, so new members are appended.</summary>
public enum ContactKind
{
    Person = 0,
    Organization = 1,
}

/// <summary>One person or one organization in one tenant.</summary>
/// <remarks>
/// <see cref="EmailKey"/> is the address trimmed and lower cased. The schema holds it unique per
/// tenant, so two contacts cannot share an address even when two creates race. A contact with no
/// address has no key and is never a duplicate of another.
///
/// A merged contact stays as a tombstone: <see cref="MergedInto"/> names the survivor, its channels
/// and fields are cleared, and it is left out of lists and delivery.
/// </remarks>
public sealed class Contact
{
    public Guid Id { get; set; }

    public ContactKind Kind { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public string? GivenName { get; set; }

    public string? FamilyName { get; set; }

    public string? Website { get; set; }

    public string? Email { get; set; }

    public string? EmailKey { get; set; }

    public string? Phone { get; set; }

    public string? Address { get; set; }

    /// <summary>Custom fields, validated against the tenant's <c>contact-record</c> content type.</summary>
    public Dictionary<string, object> Fields { get; set; } = new();

    /// <summary>Whether anonymous delivery may serve the Public fields of this contact.</summary>
    public bool IsPublished { get; set; }

    public Guid? MergedInto { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Guid CreatedBy { get; set; }
}

/// <summary>A person belonging to an organization, optionally with a role such as "Treasurer".</summary>
/// <remarks>
/// The id is derived from the pair, so linking the same two contacts twice is one row whatever the
/// timing.
/// </remarks>
public sealed class ContactLink
{
    public Guid Id { get; set; }

    public Guid PersonId { get; set; }

    public Guid OrganizationId { get; set; }

    public string? Role { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public static Guid IdFor(Guid personId, Guid organizationId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"contact-link|{personId:N}|{organizationId:N}"));
        return new Guid(hash.AsSpan(0, 16));
    }
}
