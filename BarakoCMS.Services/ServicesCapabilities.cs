using barakoCMS.Models;
using barakoCMS.Modules;

namespace BarakoCMS.Services;

/// <summary>
/// What this module's endpoints ask for instead of a role name.
/// </summary>
/// <remarks>
/// Declared here rather than in core's <c>SystemCapabilities</c>, because core does not reference
/// this module. Heartbeat is its own capability so the identity an outside service reports with
/// can hold that and nothing that edits the registry.
/// </remarks>
public static class ServicesCapabilities
{
    /// <summary>Register outside services and read their health.</summary>
    public const string ManageServices = "manage_services";

    /// <summary>Report that a registered service is alive.</summary>
    public const string SendServiceHeartbeat = "send_service_heartbeat";

    internal static readonly string[] All = [ManageServices, SendServiceHeartbeat];

    /// <summary>Who starts with these: the seeded Admin role, by its id.</summary>
    internal static readonly CapabilityDefaults Defaults = CapabilityDefaults.For(All).GrantedTo(SystemRoles.Admin);

    /// <summary>The role names the gates honour while <c>Auth:LegacyRoleFallback</c> is on.</summary>
    [Obsolete("The gates take their legacy list from the module's own capability defaults, so nothing "
            + "outside the module needs this. Removal planned for barakoCMS 6.0.")]
    public static readonly string[] LegacyRoles = [.. Defaults.LegacyRoles];
}
