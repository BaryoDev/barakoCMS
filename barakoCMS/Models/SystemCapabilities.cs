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
/// migration would encode the wrong grant. <c>GET /api/settings</c> is <c>Roles("SuperAdmin", "Admin")</c>
/// while <c>PUT /api/settings/email</c> is <c>Roles("SuperAdmin")</c>; a single <c>manage_settings</c>
/// defined now would have to pick one of those, and picking the wider one hands every Admin the
/// email configuration. Users split the same way, which is why it is three names below and not one.
/// See issues #272 and #443.
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

    /// <summary>
    /// List user accounts and reset another user's password. Narrower than the two below on
    /// purpose: these were <c>Roles("SuperAdmin")</c>, and Admin never reached them.
    /// </summary>
    public const string ManageUsers = "manage_users";

    /// <summary>Assign and remove a user's roles and groups.</summary>
    public const string ManageUserMembership = "manage_user_membership";

    /// <summary>Create, read, update and delete user groups, and change who is in them.</summary>
    public const string ManageUserGroups = "manage_user_groups";

    /// <summary>Issue, list and revoke API keys.</summary>
    public const string ManageApiKeys = "manage_api_keys";

    /// <summary>
    /// Read the audit log. Named for reading rather than managing because the surface is one GET:
    /// entries are append-only and the chain is tamper-evident, so there is nothing to manage.
    /// </summary>
    public const string ViewAuditLog = "view_audit_log";

    /// <summary>
    /// List and write system settings, and read the email settings summary. Everything Admin could
    /// already reach under <c>/api/settings</c>.
    /// </summary>
    public const string ManageSettings = "manage_settings";

    /// <summary>
    /// Change where the system's email comes from, and send a test through it.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="ManageSettings"/> rather than folded into it, because the two gates
    /// it replaces were deliberately different: reading the summary was Admin and SuperAdmin, and
    /// writing was SuperAdmin alone. Changing the sending identity redirects every password reset
    /// and every verification token in the deployment, which is a takeover rather than an
    /// administrative tweak, and it is exactly the change a compromised admin account makes. One
    /// <c>manage_settings</c> covering both would hand that to every Admin.
    /// </remarks>
    public const string ManageEmailSettings = "manage_email_settings";

    public static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        All, ManageRoles, ManageTenants, ManageTenantMembers, ManageUsers, ManageUserMembership, ManageUserGroups,
        ManageApiKeys, ViewAuditLog, ManageSettings, ManageEmailSettings,
    };

    public static bool IsKnown(string capability) =>
        !string.IsNullOrWhiteSpace(capability) && Known.Contains(capability);

    /// <summary>Does a role's granted capability set satisfy a required capability? <c>*</c> satisfies everything.</summary>
    public static bool Satisfies(IEnumerable<string> granted, string required) =>
        granted.Any(c => c == All || string.Equals(c, required, StringComparison.OrdinalIgnoreCase));

    private static readonly string[] None = [];
    private static readonly string[] Everything = [All];
    private static readonly string[] AdminDefaults =
        [ManageTenantMembers, ManageUserMembership, ManageUserGroups, ManageApiKeys, ViewAuditLog, ManageSettings];

    /// <summary>
    /// The capabilities a seeded system role starts with, chosen to match what that role could
    /// already reach before capabilities existed. Keyed on name because the role gates being
    /// replaced were keyed on name.
    /// </summary>
    /// <remarks>
    /// Admin gets only the surfaces Admin could already reach: it was never in the
    /// <c>Roles("SuperAdmin")</c> gates on roles, tenants, the user list or the password reset, so
    /// it does not get those here. A role the seeder does not create gets nothing, which is the
    /// point of the issue: a name grants no access on its own.
    /// </remarks>
    public static IReadOnlyList<string> DefaultsFor(string roleName) => roleName switch
    {
        "SuperAdmin" => Everything,
        "Admin" => AdminDefaults,
        _ => None,
    };
}
