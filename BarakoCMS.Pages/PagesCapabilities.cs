using barakoCMS.Models;
using barakoCMS.Modules;

namespace BarakoCMS.Pages;

/// <summary>What this module's endpoints ask for instead of a role name.</summary>
/// <remarks>
/// The module's settings name the content type and fields its page tree is built from, which is
/// content modelling, so they ask for the core capability that governs modelling rather than a name
/// of their own: whoever may change a type may say which type the pages are. Declared here because
/// every capability a module endpoint requires is one its module declares. See issue #443.
/// </remarks>
public static class PagesCapabilities
{
    /// <summary>Read and change the module's per-tenant settings.</summary>
    public const string ManageSettings = SystemCapabilities.ManageContentTypes;

    internal static readonly string[] All = [ManageSettings];

    /// <summary>
    /// Who starts with these: the seeded Admin role, by its id, which is the role the settings routes
    /// let in from the start. Core's own defaults already grant Admin this capability on every start,
    /// so seeding it here adds nothing; it is declared because every module declares its defaults.
    /// </summary>
    /// <remarks>
    /// SuperAdmin holds <c>*</c>, which satisfies any capability, so it is on the legacy list and
    /// deliberately not granted anything at seed.
    /// </remarks>
    internal static readonly CapabilityDefaults Defaults = CapabilityDefaults.For(All).GrantedTo(SystemRoles.Admin);
}
