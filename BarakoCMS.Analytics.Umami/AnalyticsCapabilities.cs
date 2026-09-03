namespace BarakoCMS.Analytics.Umami;

/// <summary>
/// What this module's endpoints ask for instead of a role name.
/// </summary>
/// <remarks>
/// Declared here rather than in core's <c>SystemCapabilities</c>, because core does not reference
/// this module. Nothing validates a capability name on the way into a role, so a name a module
/// declares is grantable the day the module ships. See issue #443.
/// </remarks>
public static class AnalyticsCapabilities
{
    /// <summary>Read traffic summaries, metrics, series, status and the website list.</summary>
    public const string ViewAnalytics = "view_analytics";

    /// <summary>Create a website in the upstream Umami instance.
    /// </summary>
    /// <remarks>
    /// Split from <see cref="ViewAnalytics"/> because it writes to somebody else's system
    /// using this deployment's credentials, where the other five only read. A role that watches
    /// the numbers is not the same as one that provisions against the account.
    /// </remarks>
    public const string ManageAnalyticsWebsites = "manage_analytics_websites";

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

    internal static readonly string[] All = [ViewAnalytics, ManageAnalyticsWebsites];
}
