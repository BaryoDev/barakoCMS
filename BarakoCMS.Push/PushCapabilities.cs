using barakoCMS.Models;
using barakoCMS.Modules;

namespace BarakoCMS.Push;

/// <summary>
/// What this module's endpoints ask for instead of a role name.
/// </summary>
/// <remarks>
/// Declared here rather than in core's <c>SystemCapabilities</c>, because core does not reference
/// this module.
/// </remarks>
public static class PushCapabilities
{
    /// <summary>Send a push notification to the tenant or to named users.</summary>
    public const string SendPush = "send_push";

    internal static readonly string[] All = [SendPush];

    /// <summary>Who starts with these: the seeded Admin role, by its id.</summary>
    internal static readonly CapabilityDefaults Defaults = CapabilityDefaults.For(All).GrantedTo(SystemRoles.Admin);

    /// <summary>The role names the gates honour while <c>Auth:LegacyRoleFallback</c> is on.</summary>
    [Obsolete("The gates take their legacy list from the module's own capability defaults, so nothing "
            + "outside the module needs this. Removal planned for barakoCMS 6.0.")]
    public static readonly string[] LegacyRoles = [.. Defaults.LegacyRoles];
}
