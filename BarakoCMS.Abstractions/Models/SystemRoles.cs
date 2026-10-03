namespace barakoCMS.Models;

/// <summary>
/// A role a seeder creates, named the two ways a database can hold it.
/// </summary>
/// <param name="Id">The fixed id the role is seeded under. This is the key.</param>
/// <param name="Name">
/// The name the role is seeded under. A token's role claim carries it, and it finds the role in a
/// database where no role holds <paramref name="Id"/>.
/// </param>
public sealed record SeededRole(Guid Id, string Name);

/// <summary>
/// The roles the seeder creates, identified the way the server identifies them.
/// </summary>
/// <remarks>
/// The rule that they cannot be deleted lives in <c>Features/Roles/Delete</c> and keys on the
/// seeded ids. The admin duplicated it and keyed on names instead, which is wrong in both
/// directions: rename a system role and the admin offers a delete the server refuses, create a
/// custom role called "HR" and the admin locks a role the server would happily remove.
///
/// So the id list is the single source of truth and the API says which roles are system ones,
/// rather than every client re-deriving it from names that are not the key.
///
/// Every host is seeded with SuperAdmin, Admin and User. The fourth id is the HR role of the
/// attendance demo, seeded only with the demo content. It stays in the list so a database that
/// already holds that role still refuses to delete it.
/// </remarks>
public static class SystemRoles
{
    private const string DemoHr = "00000000-0000-0000-0000-000000000003";

    public static readonly Guid SuperAdminRoleId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    public static readonly Guid AdminRoleId = Guid.Parse("00000000-0000-0000-0000-000000000002");

    [Obsolete("HR is a demo role, seeded only with the demo content, and no gate keys on its id. "
            + "Removal planned for barakoCMS 6.0.")]
    public static readonly Guid HRRoleId = Guid.Parse(DemoHr);

    public static readonly Guid UserRoleId = Guid.Parse("00000000-0000-0000-0000-000000000004");

    private static readonly Guid[] Ids =
        [SuperAdminRoleId, AdminRoleId, Guid.Parse(DemoHr), UserRoleId];

    private const string SuperAdminName = "SuperAdmin";

    /// <summary>The Admin role as a default is declared for it: its seeded id and its seeded name.</summary>
    public static readonly SeededRole Admin = new(AdminRoleId, "Admin");

    /// <summary>
    /// The role names a capability gate honours while <c>Auth:LegacyRoleFallback</c> is on, for a
    /// surface these roles reached before capabilities: each role's seeded name, then SuperAdmin.
    /// </summary>
    /// <remarks>
    /// SuperAdmin is always in the list. A list naming Admin and not SuperAdmin would lock the
    /// higher role out of a surface the lower one reaches.
    /// </remarks>
    public static IReadOnlyList<string> LegacyNames(params SeededRole[] roles)
    {
        ArgumentNullException.ThrowIfNull(roles);

        var names = new List<string>(roles.Length + 1);
        foreach (var role in roles)
        {
            if (!names.Contains(role.Name, StringComparer.Ordinal))
                names.Add(role.Name);
        }

        if (!names.Contains(SuperAdminName, StringComparer.Ordinal))
            names.Add(SuperAdminName);

        return names.AsReadOnly();
    }

    /// <summary>Whether this role is one the seeder created and the server refuses to delete.</summary>
    public static bool Contains(Guid roleId) => Array.IndexOf(Ids, roleId) >= 0;

    /// <summary>
    /// Names no custom role may take, because something still keys on them.
    /// </summary>
    /// <remarks>
    /// Ids are the key for everything the server can reach by id: PermissionResolver was moved onto
    /// them, and SensitivityService reads the caller's stored roles. One thing cannot be: the role
    /// claim in a JWT carries the name (TokenIssuer), and a capability gate's legacy role fallback
    /// reads it back with IsInRole. So a custom role called "SuperAdmin" would mint a claim that
    /// opens those gates for its holder, and a caller holding only manage_roles could create one.
    /// Reserving the names closes that at the point of writing.
    ///
    /// "HR" is not reserved. No gate names it, and what it used to decide for Sensitive fields is
    /// the <see cref="SystemCapabilities.ViewSensitive"/> capability.
    /// </remarks>
    private static readonly string[] Reserved = ["SuperAdmin", "Admin", "User"];

    /// <summary>Whether this name is reserved for a seeded role and cannot be taken by a custom one.</summary>
    public static bool IsReservedName(string? name) =>
        name is not null && Array.Exists(Reserved, r => string.Equals(r, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The message returned when a write tries to take a reserved name.</summary>
    public static string ReservedNameMessage(string name) =>
        $"'{name.Trim()}' is reserved for a built-in role. Choose another name.";
}
