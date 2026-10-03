using barakoCMS.Models;
using barakoCMS.Modules;

namespace BarakoCMS.Import;

/// <summary>
/// What this module's endpoints ask for instead of a role name.
/// </summary>
/// <remarks>
/// Declared here rather than in core's <c>SystemCapabilities</c>, because core does not reference
/// this module. See issue #443.
/// </remarks>
public static class ImportCapabilities
{
    /// <summary>
    /// Upload a spreadsheet and read back a preview grid.
    /// </summary>
    /// <remarks>
    /// One name covering the preview only. The bulk create next door is authorized on the target
    /// content type's own create permission, which is the right question for a write: it depends on
    /// what you are writing. The preview has no target yet, so it cannot ask that question, and
    /// before this it asked nothing at all: any authenticated caller could hand the server a
    /// spreadsheet to parse. Two different questions, and both worth asking.
    /// </remarks>
    public const string AnalyzeSpreadsheets = "analyze_spreadsheets";

    internal static readonly string[] All = [AnalyzeSpreadsheets];

    /// <summary>Who starts with these: the seeded Admin role, by its id.</summary>
    /// <remarks>
    /// The endpoint had no <c>Roles(...)</c> at all, so there is nothing to preserve and this is a
    /// genuine narrowing rather than a migration. Admin is granted, and SuperAdmin is on the legacy
    /// list, because they are who the import tool was for; a deployment that gave it to somebody
    /// else grants them the capability.
    /// </remarks>
    internal static readonly CapabilityDefaults Defaults = CapabilityDefaults.For(All).GrantedTo(SystemRoles.Admin);

    /// <summary>The role names the gate honours while <c>Auth:LegacyRoleFallback</c> is on.</summary>
    [Obsolete("The gate takes its legacy list from the module's own capability defaults, so nothing "
            + "outside the module needs this. Removal planned for barakoCMS 6.0.")]
    public static readonly string[] LegacyRoles = [.. Defaults.LegacyRoles];
}
