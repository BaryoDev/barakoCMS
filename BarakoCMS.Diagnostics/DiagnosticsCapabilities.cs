using barakoCMS.Models;
using barakoCMS.Modules;

namespace BarakoCMS.Diagnostics;

/// <summary>
/// What this module's endpoints ask for instead of a role name.
/// </summary>
/// <remarks>
/// Declared here rather than in core's <c>SystemCapabilities</c>, because core does not reference
/// this module. Nothing validates a capability name on the way into a role, so a name a module
/// declares is grantable the day the module ships. See issue #443.
/// </remarks>
public static class DiagnosticsCapabilities
{
    /// <summary>Read the client error list and mark one resolved.
    /// </summary>
    /// <remarks>
    /// One name, not two. Resolving is bookkeeping on the list you are reading, and triage is a
    /// single job: a role that reads the errors without being able to clear one leaves the list
    /// growing forever.
    /// </remarks>
    public const string ManageClientErrors = "manage_client_errors";

    internal static readonly string[] All = [ManageClientErrors];

    /// <summary>
    /// Who starts with these: the seeded Admin role, by its id, which is the role the old
    /// <c>Roles(...)</c> gate let in.
    /// </summary>
    /// <remarks>
    /// SuperAdmin holds <c>*</c>, which satisfies a capability from a module core has never heard
    /// of, so it is on the legacy list and deliberately not granted anything at seed.
    /// </remarks>
    internal static readonly CapabilityDefaults Defaults = CapabilityDefaults.For(All).GrantedTo(SystemRoles.Admin);

    /// <summary>The role names the gates honour while <c>Auth:LegacyRoleFallback</c> is on.</summary>
    [Obsolete("The gates take their legacy list from the module's own capability defaults, so nothing "
            + "outside the module needs this. Removal planned for barakoCMS 6.0.")]
    public static readonly string[] LegacyRoles = [.. Defaults.LegacyRoles];
}
