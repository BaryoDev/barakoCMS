using barakoCMS.Models;
using barakoCMS.Modules;

namespace BarakoCMS.Contacts;

/// <summary>What this module's endpoints ask for instead of a role name.</summary>
/// <remarks>
/// Contacts hold personal data, so reading them is a capability of its own and not part of writing.
/// </remarks>
public static class ContactsCapabilities
{
    /// <summary>Read contacts and their links, including email, phone and address.</summary>
    public const string ReadContacts = "read_contacts";

    /// <summary>Create, change, merge and delete contacts and their links.</summary>
    public const string ManageContacts = "manage_contacts";

    internal static readonly string[] All = [ReadContacts, ManageContacts];

    /// <summary>Who starts with these: the seeded Admin role, by its id.</summary>
    internal static readonly CapabilityDefaults Defaults = CapabilityDefaults.For(All).GrantedTo(SystemRoles.Admin);
}
