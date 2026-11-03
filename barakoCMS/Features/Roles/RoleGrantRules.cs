using barakoCMS.Core.Interfaces;
using barakoCMS.Features.Users;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Features.Roles;

/// <summary>
/// The hold check shared by every route that hands roles to someone else (service principals and
/// tenant members): the caller must hold each capability and each content right the roles carry.
/// </summary>
internal static class RoleGrantRules
{
    /// <summary>
    /// The first reason the caller cannot hand these roles to a principal or a member, or null. A person
    /// may only grant what they hold: otherwise a key steward or a members manager could hand out a
    /// capability or content right they lack and use it themselves. A SuperAdmin holds every capability
    /// the roles can carry, since platform capabilities are already refused above.
    /// </summary>
    public static async Task<string?> NotHeldAsync(
        IQuerySession session, ICallerCapabilities callers, IPermissionResolver permissions, System.Security.Claims.ClaimsPrincipal user,
        IEnumerable<Guid> roleIds, string tenant, CancellationToken ct)
    {
        var ids = roleIds.ToList();
        if (ids.Count == 0 || await PlatformRoles.IsSuperAdminAsync(session, user, ct))
            return null;

        var roles = await session.Query<Role>().Where(r => r.DeletedAt == null && ids.Contains(r.Id)).ToListAsync(ct);
        foreach (var capability in roles.SelectMany(r => r.SystemCapabilities).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!await callers.HoldsAsync(user, capability, ct))
                return $"You cannot grant '{capability}' because you do not hold it.";
        }

        // Content rights too. A key acts through the principal's roles on the content routes, so a
        // role with wider rights than the caller's own would widen the caller. The resolver those
        // routes use must grant the action, and one of the caller's own rules for the same type and
        // action must also be at least as wide as the granted rule (see Covers).
        Guid.TryParse(user.FindFirst("UserId")?.Value, out var callerId);
        var caller = callerId == Guid.Empty ? null : await session.LoadAsync<User>(callerId, ct);
        IReadOnlyList<Role> callerRoles = [];
        if (caller is not null)
        {
            var (callerRoleIds, _) = await barakoCMS.Infrastructure.Multitenancy.MembershipRoles
                .ResolveAsync(session, caller, tenant, ct);
            callerRoles = callerRoleIds.Count == 0
                ? []
                : await session.Query<Role>().Where(r => r.DeletedAt == null && callerRoleIds.Contains(r.Id)).ToListAsync(ct);
        }

        // The seeded Admin system role (matched by id, as the resolver matches SuperAdmin) counts as
        // holding every content right and field list in its tenant. Capabilities above are still exact.
        var tenantAdmin = callerRoles.Any(r => r.Id == SystemRoles.AdminRoleId);
        foreach (var (type, action, rule) in roles.SelectMany(ContentRights).Distinct())
        {
            if (tenantAdmin)
                break;
            var held = caller is not null
                && await permissions.CanPerformActionAsync(caller, type, action, null, ct)
                && callerRoles.Any(r => r.Id == SystemRoles.SuperAdminRoleId || HoldsRule(r, type, action, rule));
            if (!held)
                return $"You cannot grant '{action}' on '{type}' because you do not hold it.";
        }

        return null;
    }

    /// <summary>Every enabled content rule a role carries: each type with create, read, update, delete and named transitions.</summary>
    private static IEnumerable<(string Type, string Action, PermissionRule Rule)> ContentRights(Role role)
    {
        foreach (var permission in role.Permissions ?? [])
        {
            if (permission is null || string.IsNullOrWhiteSpace(permission.ContentTypeSlug)) continue;
            var type = permission.ContentTypeSlug;
            if (permission.Create?.Enabled == true) yield return (type, "create", permission.Create);
            if (permission.Read?.Enabled == true) yield return (type, "read", permission.Read);
            if (permission.Update?.Enabled == true) yield return (type, "update", permission.Update);
            if (permission.Delete?.Enabled == true) yield return (type, "delete", permission.Delete);
            foreach (var (name, rule) in permission.Transitions ?? [])
                if (rule?.Enabled == true) yield return (type, PermissionResolver.TransitionActionPrefix + name, rule);
        }
    }

    // The rule a role holds for a type and action, found the way PermissionResolver finds it.
    private static bool HoldsRule(Role role, string type, string action, PermissionRule granted)
    {
        var permission = PermissionResolver.PermissionFor(role.Permissions, type);
        if (permission is null) return false;
        PermissionRule? rule;
        if (action.StartsWith(PermissionResolver.TransitionActionPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var name = action[PermissionResolver.TransitionActionPrefix.Length..];
            rule = permission.Transitions?.FirstOrDefault(t => string.Equals(t.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
        }
        else
        {
            rule = action.ToLowerInvariant() switch
            {
                "create" => permission.Create,
                "read" => permission.Read,
                "update" => permission.Update,
                "delete" => permission.Delete,
                _ => null,
            };
        }

        return rule is { Enabled: true } && Covers(rule, granted, action);
    }

    /// <summary>
    /// Whether the caller's rule is at least as wide as the granted one. Conditions: the caller's
    /// has none, or both are equal (no subset proof is tried). Fields: a null list is every field
    /// (FieldSet.Of), and an empty list is no field, so the caller's list must be null or contain
    /// every field of the granted one, and a null granted list needs a null caller list. Read uses
    /// ReadableFields, create and update use WritableFields, as the resolver's FieldSetAsync does.
    /// </summary>
    internal static bool Covers(PermissionRule held, PermissionRule granted, string action)
    {
        var noConditions = held.Conditions is null || held.Conditions.Count == 0;
        if (!noConditions && !ConditionsEqual(held.Conditions, granted.Conditions))
            return false;

        if (action.Equals("delete", StringComparison.OrdinalIgnoreCase)
            || action.StartsWith(PermissionResolver.TransitionActionPrefix, StringComparison.OrdinalIgnoreCase))
            return true;

        var reading = action.Equals("read", StringComparison.OrdinalIgnoreCase);
        var have = FieldSet.Of(reading ? held.ReadableFields : held.WritableFields);
        var want = FieldSet.Of(reading ? granted.ReadableFields : granted.WritableFields);
        return have.IsAll || (!want.IsAll && want.Names!.All(have.Allows));
    }

    private static bool ConditionsEqual(Dictionary<string, object>? a, Dictionary<string, object>? b)
    {
        if (a is null || a.Count == 0) return b is null || b.Count == 0;
        if (b is null || b.Count == 0) return false;
        var x = System.Text.Json.JsonSerializer.SerializeToNode(a);
        var y = System.Text.Json.JsonSerializer.SerializeToNode(b);
        return System.Text.Json.Nodes.JsonNode.DeepEquals(x, y);
    }
}
