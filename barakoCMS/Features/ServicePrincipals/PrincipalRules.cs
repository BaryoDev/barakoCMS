using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Features.Roles;
using barakoCMS.Features.Tenants.Members;
using barakoCMS.Features.Users;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.ServicePrincipals;

// A service principal is an identity for a machine caller. Its API keys take their roles from the
// principal, so they keep working when the person who made them leaves. Managed by a person holding
// manage_api_keys, in that person's tenant. A principal key cannot reach these routes: managing keys
// and principals is on the cap: deny list.

internal static class PrincipalRules
{
    public const string PlatformRoleMessage =
        "A service principal cannot hold a platform role or a role that carries a platform capability.";

    /// <summary>
    /// The first reason these roles cannot go on a principal, or null. It reuses the rule a person
    /// follows when assigning roles to a membership (<see cref="Members.IsAssignable"/> and
    /// <see cref="RoleScope.AllReachableAsync"/>, so another tenant's role answers as one that does
    /// not exist) and is stricter in one way: a role carrying a platform capability is refused for
    /// every caller, SuperAdmin included, because a key must never hold platform reach.
    /// </summary>
    public static async Task<string?> RefusalAsync(IQuerySession session, List<Guid> roleIds, string tenant, CancellationToken ct)
    {
        if (roleIds.Any(id => !Members.IsAssignable(id)))
            return "SuperAdmin is a platform role and cannot be granted inside a tenant.";
        if (!await RoleScope.AllReachableAsync(session, roleIds, tenant, ct))
            return "One or more roles do not exist.";
        if (roleIds.Count == 0)
            return null;

        var roles = await session.Query<Role>().Where(r => r.DeletedAt == null && roleIds.Contains(r.Id)).ToListAsync(ct);
        return roles.Any(r => r.IsPlatform || PlatformRoles.CarriesPlatformCapability(r)) ? PlatformRoleMessage : null;
    }

    /// <summary>
    /// The first reason the caller cannot hand these roles to a principal, or null. A person may
    /// only grant what they hold, as for stations: otherwise a key steward could mint a principal
    /// with a capability they lack and use it through a key. A SuperAdmin holds every capability
    /// the roles can carry, since platform capabilities are already refused above.
    /// </summary>
    public static async Task<string?> NotHeldAsync(
        IQuerySession session, ICallerCapabilities callers, IPermissionResolver permissions, System.Security.Claims.ClaimsPrincipal user,
        IEnumerable<Guid> roleIds, CancellationToken ct)
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
                .ResolveAsync(session, caller, TenantOf(user), ct);
            callerRoles = callerRoleIds.Count == 0
                ? []
                : await session.Query<Role>().Where(r => r.DeletedAt == null && callerRoleIds.Contains(r.Id)).ToListAsync(ct);
        }

        foreach (var (type, action, rule) in roles.SelectMany(ContentRights).Distinct())
        {
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

    /// <summary>Whether these roles may go on a principal, then whether the caller holds what they carry.</summary>
    public static async Task<string?> CheckAsync(
        IQuerySession session, ICallerCapabilities callers, IPermissionResolver permissions, System.Security.Claims.ClaimsPrincipal user,
        List<Guid> roleIds, string tenant, CancellationToken ct) =>
        await RefusalAsync(session, roleIds, tenant, ct) ?? await NotHeldAsync(session, callers, permissions, user, roleIds, ct);

    /// <summary>The trimmed name, or the reason it is refused.</summary>
    public static string? NameProblem(string? raw, out string name)
    {
        name = (raw ?? string.Empty).Trim();
        if (name.Length is 0 or > 100)
            return "Name is required, up to 100 characters.";
        return name.Any(char.IsControl) ? "Name cannot contain control characters." : null;
    }

    /// <summary>
    /// Saves, and answers 409 when the principal changed since it was read (another admin edited or
    /// disabled it at the same time). Returns false when it answered.
    /// </summary>
    public static async Task<bool> SaveAsync(IDocumentSession session, IEndpoint endpoint, CancellationToken ct)
    {
        try
        {
            await session.SaveChangesAsync(ct);
            return true;
        }
        catch (JasperFx.ConcurrencyException)
        {
            endpoint.HttpContext.Response.StatusCode = 409;
            await endpoint.HttpContext.Response.WriteAsJsonAsync(
                new { message = "The service principal changed while it was being updated. Try again." }, ct);
            return false;
        }
    }

    /// <summary>The principal's roles, loaded for the hold check on bind and move.</summary>
    public static Task<string?> NotHeldByPrincipalAsync(
        IQuerySession session, ICallerCapabilities callers, IPermissionResolver permissions, System.Security.Claims.ClaimsPrincipal user,
        ServicePrincipal principal, CancellationToken ct) =>
        NotHeldAsync(session, callers, permissions, user, principal.RoleIds, ct);

    public static string TenantOf(System.Security.Claims.ClaimsPrincipal user) =>
        (user.FindFirst("tenant")?.Value ?? Tenant.DefaultSlug).Trim().ToLowerInvariant();

    public static Guid? ActorOf(System.Security.Claims.ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirst("UserId")?.Value, out var id) ? id : null;

    /// <summary>The principal in this tenant, or null when it is missing or belongs to another tenant.</summary>
    public static Task<ServicePrincipal?> InTenantAsync(IQuerySession session, Guid id, string tenant, CancellationToken ct) =>
        session.Query<ServicePrincipal>().FirstOrDefaultAsync(p => p.Id == id && p.TenantSlug == tenant, ct);
}

internal sealed record ServicePrincipalResponse(
    Guid Id, string Name, string TenantSlug, List<Guid> RoleIds, bool Disabled, DateTime? DisabledAt,
    DateTime CreatedAt, Guid CreatedBy)
{
    public static ServicePrincipalResponse Of(ServicePrincipal p) =>
        new(p.Id, p.Name, p.TenantSlug, p.RoleIds, p.Disabled, p.DisabledAt, p.CreatedAt, p.CreatedBy);
}
