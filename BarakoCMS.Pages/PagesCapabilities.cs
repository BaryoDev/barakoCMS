using barakoCMS.Models;

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
}
