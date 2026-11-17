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
        foreach (var capability in roles.SelectMany(r => r.SystemCapabilities ?? []).Distinct(StringComparer.OrdinalIgnoreCase))
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
        // holding every content right and field list in its tenant, but only for a granted role owned by
        // that tenant. A global or platform role reaches other tenants, so it gets the full per-right check.
        var tenantAdmin = callerRoles.Any(r => r.Id == SystemRoles.AdminRoleId);
        var rights = roles.SelectMany(r => ContentRights(r).Select(x =>
            (x.Type, x.Action, x.Rule, Local: !r.IsPlatform && string.Equals(r.TenantSlug, tenant, StringComparison.OrdinalIgnoreCase)))).Distinct();
        foreach (var (type, action, rule, local) in rights)
        {
            if (tenantAdmin && local)
                continue;
            var held = caller is not null
                && await permissions.CanPerformActionAsync(caller, type, action, null, ct)
                && callerRoles.Any(r => r.Id == SystemRoles.SuperAdminRoleId || HoldsRule(r, type, action, rule));
            if (!held)
                return $"You cannot grant '{action}' on '{type}' because you do not hold it.";
        }

        return null;
    }

    /// <summary>
    /// The first reason the caller cannot make this profile change, or null. A condition such as
    /// <c>Branch eq $CURRENT_USER.branches</c> reads the member's profile, so a profile value a role
    /// reads widens what the role allows, and the caller must hold it, as for a role grant. The
    /// member's roles are the requested ones unioned with their global and group roles. For a role the
    /// member keeps, only a value that is added or changed counts. For a role this request grants (one
    /// not in <paramref name="keptRoleIds"/>), every attribute it reads in the effective profile
    /// (<paramref name="after"/>, or <paramref name="before"/> when none is sent) counts, changed or not.
    /// <c>branches</c> is always read, since the branch guards read it whatever a role says. It is held
    /// when each branch counted is one of the caller's own; any other attribute is held when the
    /// caller's own value is identical. A SuperAdmin passes, and so does the tenant's Admin for roles
    /// owned by the tenant and for <c>branches</c>, as for a role grant.
    /// </summary>
    public static async Task<string?> ProfileNotHeldAsync(
        IQuerySession session, System.Security.Claims.ClaimsPrincipal user, Guid memberId,
        IEnumerable<Guid> roleIds, IReadOnlyCollection<Guid> keptRoleIds,
        IReadOnlyDictionary<string, string>? before, IReadOnlyDictionary<string, string>? after,
        string tenant, CancellationToken ct)
    {
        if (await PlatformRoles.IsSuperAdminAsync(session, user, ct))
            return null;

        var requested = roleIds.Distinct().ToList();
        var member = await session.LoadAsync<User>(memberId, ct);
        var effectiveIds = await barakoCMS.Infrastructure.Multitenancy.MembershipRoles
            .WithRequestedAsync(session, member, tenant, requested, ct);
        var roles = effectiveIds.Count == 0
            ? []
            : await session.Query<Role>().Where(r => r.DeletedAt == null && effectiveIds.Contains(r.Id)).ToListAsync(ct);

        Guid.TryParse(user.FindFirst("UserId")?.Value, out var callerId);
        var caller = callerId == Guid.Empty ? null : await session.LoadAsync<User>(callerId, ct);
        var tenantAdmin = false;
        if (caller is not null)
        {
            var (callerRoleIds, _) = await barakoCMS.Infrastructure.Multitenancy.MembershipRoles
                .ResolveAsync(session, caller, tenant, ct);
            tenantAdmin = callerRoleIds.Contains(SystemRoles.AdminRoleId);
        }

        var counted = roles.Where(r => !(tenantAdmin && !r.IsPlatform
            && string.Equals(r.TenantSlug, tenant, StringComparison.OrdinalIgnoreCase))).ToList();
        var effective = after ?? before;
        var changed = ChangedNames(before, after);
        var readByAny = ReadNames(counted);
        var granted = counted.Where(r => requested.Contains(r.Id) && !keptRoleIds.Contains(r.Id)).ToList();
        var readByNew = ReadNames(granted);
        if (!tenantAdmin)
        {
            readByAny.Add(CallerBranches.BranchesAttribute);
            // The branch guard scopes every right by the member's branches, whether or not a rule reads them,
            // so a role granted now with rights needs every branch the member has, not only the added ones.
            if (granted.Any(CarriesRights))
                readByNew.Add(CallerBranches.BranchesAttribute);
        }

        var checks = changed.Where(readByAny.Contains)
            .Select(name => (name, full: false))
            .Concat((effective?.Keys ?? Enumerable.Empty<string>()).Where(readByNew.Contains).Select(name => (name, full: true)))
            .ToList();
        if (checks.Count == 0)
            return null;

        var own = caller is null
            ? null
            : (await session.Query<Membership>()
                .FirstOrDefaultAsync(m => m.UserId == callerId && m.TenantSlug == tenant && m.Status == MembershipStatus.Active, ct))?.Profile;

        foreach (var (name, full) in checks)
        {
            if (!ProfileValueHeld(name, full, before, effective, own))
                return $"You cannot set '{name}' because you do not hold that value and a role of this member reads it.";
        }

        return null;
    }

    private static List<string> ChangedNames(
        IReadOnlyDictionary<string, string>? before, IReadOnlyDictionary<string, string>? after) =>
        (after ?? new Dictionary<string, string>())
            .Where(pair => !(before is not null && before.TryGetValue(pair.Key, out var old)
                             && string.Equals(old, pair.Value, StringComparison.Ordinal)))
            .Select(pair => pair.Key)
            .ToList();

    private static HashSet<string> ReadNames(IEnumerable<Role> roles) =>
        roles.SelectMany(r => ContentRights(r).SelectMany(x => CallerAttributes.NamesRead(x.Rule.Conditions)))
            .ToHashSet(StringComparer.Ordinal);

    // The value is compared as stored, as the condition evaluator reads it: no trimming, ordinal.
    private static bool ProfileValueHeld(
        string name, bool full, IReadOnlyDictionary<string, string>? before,
        IReadOnlyDictionary<string, string>? effective, IReadOnlyDictionary<string, string>? own)
    {
        if (name == CallerBranches.BranchesAttribute)
        {
            var kept = full ? [] : CallerBranches.Of(before).ToHashSet();
            var mine = CallerBranches.Of(own).ToHashSet();
            return CallerBranches.Of(effective).All(b => kept.Contains(b) || mine.Contains(b));
        }

        return own is not null && effective is not null && own.TryGetValue(name, out var value)
            && string.Equals(value, effective[name], StringComparison.Ordinal);
    }

    public const string GlobalRoleWithRightsMessage =
        "This role carries capabilities or content rights and would apply in every tenant, so only a platform administrator (SuperAdmin) can grant it globally.";

    /// <summary>Whether a role carries any capability or any enabled content right.</summary>
    public static bool CarriesRights(Role role) =>
        (role.SystemCapabilities ?? []).Count > 0 || ContentRights(role).Any();

    /// <summary>
    /// The refusal for a non-SuperAdmin changing a global role set (a user's global roles or a group's
    /// roles, which reach every tenant): any role in it that carries rights. No per-tenant proof is
    /// tried, because a hold in one tenant says nothing about the others.
    /// </summary>
    public static async Task<string?> GlobalRightsRefusalAsync(
        IQuerySession session, System.Security.Claims.ClaimsPrincipal user, IEnumerable<Guid> roleIds, CancellationToken ct)
    {
        var ids = roleIds.Distinct().ToList();
        if (ids.Count == 0 || await PlatformRoles.IsSuperAdminAsync(session, user, ct))
            return null;
        var roles = await session.Query<Role>().Where(r => r.DeletedAt == null && ids.Contains(r.Id)).ToListAsync(ct);
        return roles.Any(CarriesRights) ? GlobalRoleWithRightsMessage : null;
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
