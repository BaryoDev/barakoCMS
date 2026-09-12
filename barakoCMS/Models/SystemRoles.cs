namespace barakoCMS.Models;

/// <summary>
/// The four roles the seeder creates, identified the way the server identifies them.
/// </summary>
/// <remarks>
/// The rule that they cannot be deleted lives in <c>Features/Roles/Delete</c> and keys on the
/// seeded ids. The admin duplicated it and keyed on names instead, which is wrong in both
/// directions: rename a system role and the admin offers a delete the server refuses, create a
/// custom role called "HR" and the admin locks a role the server would happily remove.
///
/// So the id list is the single source of truth and the API says which roles are system ones,
/// rather than every client re-deriving it from names that are not the key.
/// </remarks>
public static class SystemRoles
{
    public static readonly Guid SuperAdminRoleId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    public static readonly Guid AdminRoleId = Guid.Parse("00000000-0000-0000-0000-000000000002");
    public static readonly Guid HRRoleId = Guid.Parse("00000000-0000-0000-0000-000000000003");
    public static readonly Guid UserRoleId = Guid.Parse("00000000-0000-0000-0000-000000000004");

    private static readonly Guid[] Ids =
        [SuperAdminRoleId, AdminRoleId, HRRoleId, UserRoleId];

    /// <summary>Whether this role is one the seeder created and the server refuses to delete.</summary>
    public static bool Contains(Guid roleId) => Array.IndexOf(Ids, roleId) >= 0;

    /// <summary>
    /// Names no custom role may take, because something still keys on them.
    /// </summary>
    /// <remarks>
    /// Ids are the key for everything the server can reach by id, and PermissionResolver was moved
    /// onto them. Two things cannot be: the role claim in a JWT carries the name (TokenIssuer), and
    /// SensitivityService reads it back with IsInRole. So a custom role called "SuperAdmin" used to
    /// mint a claim that turned off sensitivity scrubbing for its holder, and a caller holding only
    /// manage_roles could create one. Reserving the names closes that at the point of writing,
    /// which is the only place both paths pass through.
    /// </remarks>
    private static readonly string[] Reserved = ["SuperAdmin", "Admin", "HR", "User"];

    /// <summary>Whether this name is reserved for a seeded role and cannot be taken by a custom one.</summary>
    public static bool IsReservedName(string? name) =>
        name is not null && Array.Exists(Reserved, r => string.Equals(r, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The message returned when a write tries to take a reserved name.</summary>
    public static string ReservedNameMessage(string name) =>
        $"'{name.Trim()}' is reserved for a built-in role. Choose another name.";
}
