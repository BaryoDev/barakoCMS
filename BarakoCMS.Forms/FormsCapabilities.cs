namespace BarakoCMS.Forms;

/// <summary>What this module's authenticated endpoints ask for instead of a role name.</summary>
public static class FormsCapabilities
{
    /// <summary>Mark a content type as accepting public submissions, or stop it accepting them.</summary>
    public const string ManageForms = "manage_forms";

    /// <summary>Roles that pass the gate by name while the legacy role fallback is on.</summary>
    public static readonly string[] LegacyRoles = ["Admin", "SuperAdmin"];

    internal static readonly string[] SeededRoles = ["Admin"];

    internal static readonly string[] All = [ManageForms];
}
