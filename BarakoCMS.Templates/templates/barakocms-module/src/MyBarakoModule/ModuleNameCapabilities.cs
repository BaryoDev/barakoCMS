namespace MyBarakoModule;

/// <summary>
/// What this module's endpoints ask for. A capability rather than a role name, so a deployment
/// can hand it to any role it creates. The names carry the module's own name so they cannot
/// collide with core's or another module's.
/// </summary>
public static class ModuleNameCapabilities
{
    /// <summary>List the module's notes.</summary>
    public const string ReadNotes = "read_modulename_notes";

    /// <summary>
    /// The roles the seeder grants everything to. Admin only: SuperAdmin holds the wildcard and
    /// satisfies any capability without being named.
    /// </summary>
    internal static readonly string[] SeededRoles = ["Admin"];

    internal static readonly string[] All = [ReadNotes];
}
