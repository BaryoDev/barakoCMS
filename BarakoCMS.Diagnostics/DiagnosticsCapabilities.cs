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

    /// <summary>
    /// The roles that reached these endpoints before the migration, which is what the old
    /// <c>Roles(...)</c> gate listed.
    /// </summary>
    /// <remarks>
    /// SuperAdmin holds <c>*</c>, which satisfies a capability from a module core has never heard
    /// of, so it is listed as a legacy fallback and deliberately not granted anything at seed.
    /// </remarks>
    public static readonly string[] LegacyRoles = ["Admin", "SuperAdmin"];

    internal static readonly string[] SeededRoles = ["Admin"];

    internal static readonly string[] All = [ManageClientErrors];
}
