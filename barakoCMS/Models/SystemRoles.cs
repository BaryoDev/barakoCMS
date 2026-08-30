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
}
