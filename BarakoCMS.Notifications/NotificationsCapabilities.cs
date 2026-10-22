using barakoCMS.Models;
using barakoCMS.Modules;

namespace BarakoCMS.Notifications;

/// <summary>What this module's gated endpoints ask for instead of a role name.</summary>
public static class NotificationsCapabilities
{
    /// <summary>Raise a notification for any user of the tenant, and read every user's.</summary>
    public const string ManageNotifications = "manage_notifications";

    internal static readonly string[] All = [ManageNotifications];

    /// <summary>Who starts with these: the seeded Admin role, by its id.</summary>
    internal static readonly CapabilityDefaults Defaults = CapabilityDefaults.For(All).GrantedTo(SystemRoles.Admin);

    /// <summary>Roles that pass the gate by name while the legacy role fallback is on.</summary>
    [Obsolete("The gates take their legacy list from the module's own capability defaults, so nothing "
            + "outside the module needs this. Removal planned for barakoCMS 6.0.")]
    public static readonly string[] LegacyRoles = [.. Defaults.LegacyRoles];
}
