namespace barakoCMS.Models;

/// <summary>
/// The system-wide capabilities a <see cref="Role"/> can hold in <see cref="Role.SystemCapabilities"/>,
/// and the rule for whether a role's granted set satisfies a required one. Modelled on
/// <see cref="ApiKeyScopes"/>, which already carries both the known set and the satisfies rule.
/// </summary>
/// <remarks>
/// A capability names an administrative surface, not an HTTP verb. It is what an administrative
/// endpoint asks for instead of a role name, so a role created through <c>POST /api/roles</c> can be
/// granted access without a code change, and a role called "Editor" gains nothing by its name.
///
/// The vocabulary is deliberately short. It covers the surfaces migrated so far and grows one area
/// at a time, because the role gates it replaces are not uniform and a name invented ahead of the
/// migration would encode the wrong grant. <c>GET /api/users</c> is <c>Roles("SuperAdmin")</c> while
/// <c>POST /api/users/{id}/roles</c> is <c>Roles("SuperAdmin", "Admin")</c>; a single
/// <c>manage_users</c> defined now would have to pick one of those, and picking the wider one hands
/// every Admin the user list. Settings splits the same way. See issue #272.
/// </remarks>
public static class SystemCapabilities
{
    /// <summary>Satisfies every capability, including ones added after the role was written.</summary>
    public const string All = "*";

    /// <summary>Create, read, update and delete roles.</summary>
    public const string ManageRoles = "manage_roles";

    /// <summary>List, create and update tenants.</summary>
    public const string ManageTenants = "manage_tenants";

    /// <summary>List and change who belongs to a tenant and with which roles.</summary>
    public const string ManageTenantMembers = "manage_tenant_members";

    public static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        All, ManageRoles, ManageTenants, ManageTenantMembers,
    };

    public static bool IsKnown(string capability) =>
        !string.IsNullOrWhiteSpace(capability) && Known.Contains(capability);

    /// <summary>Does a role's granted capability set satisfy a required capability? <c>*</c> satisfies everything.</summary>
    public static bool Satisfies(IEnumerable<string> granted, string required) =>
        granted.Any(c => c == All || string.Equals(c, required, StringComparison.OrdinalIgnoreCase));

    private static readonly string[] None = [];
    private static readonly string[] Everything = [All];
    private static readonly string[] AdminDefaults = [ManageTenantMembers];

    /// <summary>
    /// The capabilities a seeded system role starts with, chosen to match what that role could
    /// already reach before capabilities existed. Keyed on name because the role gates being
    /// replaced were keyed on name.
    /// </summary>
    /// <remarks>
    /// Admin gets only the surfaces Admin could already reach: it was never in the
    /// <c>Roles("SuperAdmin")</c> gates on roles and tenants, so it does not get those here.
    /// A role the seeder does not create gets nothing, which is the point of the issue: a name
    /// grants no access on its own.
    /// </remarks>
    public static IReadOnlyList<string> DefaultsFor(string roleName) => roleName switch
    {
        "SuperAdmin" => Everything,
        "Admin" => AdminDefaults,
        _ => None,
    };
}
