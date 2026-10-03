using barakoCMS.Models;
using barakoCMS.Modules;

namespace BarakoCMS.Forms;

/// <summary>What this module's authenticated endpoints ask for instead of a role name.</summary>
public static class FormsCapabilities
{
    /// <summary>Mark a content type as accepting public submissions, or stop it accepting them.</summary>
    public const string ManageForms = "manage_forms";

    internal static readonly string[] All = [ManageForms];

    /// <summary>Who starts with these: the seeded Admin role, by its id.</summary>
    internal static readonly CapabilityDefaults Defaults = CapabilityDefaults.For(All).GrantedTo(SystemRoles.Admin);

    /// <summary>Roles that pass the gate by name while the legacy role fallback is on.</summary>
    [Obsolete("The gates take their legacy list from the module's own capability defaults, so nothing "
            + "outside the module needs this. Removal planned for barakoCMS 6.0.")]
    public static readonly string[] LegacyRoles = [.. Defaults.LegacyRoles];
}
