namespace BarakoCMS.AI;

/// <summary>
/// What this module's endpoints ask for instead of a role name.
/// </summary>
/// <remarks>
/// Declared here rather than in core's <c>SystemCapabilities</c>, because core does not reference
/// this module. Nothing validates a capability name on the way into a role, so a name a module
/// declares is grantable the day the module ships. See issue #443.
/// </remarks>
public static class AiCapabilities
{
    /// <summary>Rebuild the embedding index for a content type.</summary>
    public const string ManageSearchIndex = "manage_search_index";

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

    internal static readonly string[] All = [ManageSearchIndex];
}
