using barakoCMS.Models;
using barakoCMS.Modules;

namespace BarakoCMS.Reports;

/// <summary>What this module's authenticated endpoints ask for instead of a role name.</summary>
public static class ReportsCapabilities
{
    /// <summary>Save a report definition and list the saved ones.</summary>
    /// <remarks>
    /// Running a report needs no capability. It needs a signed-in caller, and the caller's own read
    /// rules decide what the report may group, measure and count.
    /// </remarks>
    public const string ManageReports = "manage_reports";

    internal static readonly string[] All = [ManageReports];

    /// <summary>Who starts with these: the seeded Admin role, by its id.</summary>
    internal static readonly CapabilityDefaults Defaults = CapabilityDefaults.For(All).GrantedTo(SystemRoles.Admin);
}
