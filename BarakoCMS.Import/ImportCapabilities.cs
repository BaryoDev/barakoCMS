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

    /// <summary>
    /// The roles that reached the endpoint before the gate existed.
    /// </summary>
    /// <remarks>
    /// It had no <c>Roles(...)</c> at all, so there is nothing to preserve and this is a genuine
    /// narrowing rather than a migration. Admin and SuperAdmin are named because they are who the
    /// import tool was for; a deployment that gave it to somebody else grants them the capability.
    /// </remarks>
    public static readonly string[] LegacyRoles = ["Admin", "SuperAdmin"];

    internal static readonly string[] SeededRoles = ["Admin"];

    internal static readonly string[] All = [AnalyzeSpreadsheets];
}
