# User groups grant roles (#919)

Decision: groups grant. A group holds role ids, and every member holds those roles in addition to
their own. The alternative, removing groups, breaks the HTTP contract and `User.GroupIds` and
`UserGroup`, and changes what existing deployments have. Wiring groups in is additive and
section 3 of CLAUDE.md says to default to what happens today.

## What it does

- `UserGroup.RoleIds` is new and empty by default. A group created before this holds no roles, so
  nobody's access changes.
- `MembershipRoles.ResolveAsync` unions the roles of the user's groups into the roles the user
  already holds. Permissions, read predicates, field sets and capabilities all resolve roles
  there, so a group role works for every one of them.
- A user is a member when `User.GroupIds` names the group or the group's `UserIds` lists the user.
  The two add endpoints write different sides, so both count. `DELETE /api/users/{id}/groups/{groupId}`
  now clears both sides.
- Nested groups are not followed. A child group does not inherit the roles of its parent.
- Group roles count as global roles. A group is not owned by a tenant, so a tenant-owned role is
  refused when it is set, and one that got there anyway grants nothing outside its own tenant.

## Setting roles

`POST /api/user-groups` accepts `roleIds`. `PUT /api/user-groups/{id}` accepts `roleIds` too, and
leaves the roles alone when it is absent, so existing callers keep working. Responses carry
`roleIds`. Both are optional fields, so neither request nor response breaks.

Changing a group's roles is as sensitive as assigning a global role to a user, so it follows the
same rules as `POST /api/users/{id}/roles`. The caller must hold manage_user_membership through a
global role, a role carrying a platform capability (or SuperAdmin) can only be added or removed by
a SuperAdmin, and the role has to exist.

## Not done

Conditions that test group membership. Roles are the grant path; a condition can be added later
without changing this.
