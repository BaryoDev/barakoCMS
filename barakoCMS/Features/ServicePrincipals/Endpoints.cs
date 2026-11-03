using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Features.Roles;
using barakoCMS.Features.Tenants.Members;
using barakoCMS.Features.Users;
using barakoCMS.Infrastructure.Audit;
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
        IQuerySession session, ICallerCapabilities callers, System.Security.Claims.ClaimsPrincipal user,
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

        return null;
    }

    /// <summary>Whether these roles may go on a principal, then whether the caller holds what they carry.</summary>
    public static async Task<string?> CheckAsync(
        IQuerySession session, ICallerCapabilities callers, System.Security.Claims.ClaimsPrincipal user,
        List<Guid> roleIds, string tenant, CancellationToken ct) =>
        await RefusalAsync(session, roleIds, tenant, ct) ?? await NotHeldAsync(session, callers, user, roleIds, ct);

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
        IQuerySession session, ICallerCapabilities callers, System.Security.Claims.ClaimsPrincipal user,
        ServicePrincipal principal, CancellationToken ct) =>
        NotHeldAsync(session, callers, user, principal.RoleIds, ct);

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

internal sealed class CreateServicePrincipalRequest
{
    public string Name { get; set; } = string.Empty;
    public List<Guid>? RoleIds { get; set; }
}

/// <summary>POST /api/service-principals</summary>
internal class CreateServicePrincipalEndpoint(IDocumentSession session, ICallerCapabilities callers)
    : Endpoint<CreateServicePrincipalRequest, ServicePrincipalResponse>
{
    public override void Configure()
    {
        Post("/api/service-principals");
        Definition.RequireCapability(SystemCapabilities.ManageApiKeys, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(CreateServicePrincipalRequest req, CancellationToken ct)
    {
        var tenant = PrincipalRules.TenantOf(User);
        if (PrincipalRules.NameProblem(req.Name, out var name) is { } nameProblem)
            ThrowError(r => r.Name, nameProblem);

        var roleIds = (req.RoleIds ?? []).Distinct().ToList();
        if (await PrincipalRules.CheckAsync(session, callers, User, roleIds, tenant, ct) is { } why)
            ThrowError(r => r.RoleIds!, why, 403);

        var actor = PrincipalRules.ActorOf(User);
        var principal = new ServicePrincipal
        {
            Name = name,
            TenantSlug = tenant,
            RoleIds = roleIds,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = actor ?? Guid.Empty,
        };
        session.Store(principal);
        await AuditLog.RecordAsync(session, tenant, "service_principal.created", actor, User.FindFirst("Username")?.Value,
            targetType: "ServicePrincipal", targetId: principal.Id.ToString(),
            metadata: new() { ["name"] = principal.Name, ["roleIds"] = roleIds.Select(r => r.ToString()).ToList() }, ct: ct);
        await session.SaveChangesAsync(ct);

        await Send.OkAsync(ServicePrincipalResponse.Of(principal), ct);
    }
}

/// <summary>GET /api/service-principals</summary>
internal class ListServicePrincipalsEndpoint(IQuerySession session)
    : Endpoint<ListRequest, PaginatedResponse<ServicePrincipalResponse>>
{
    public override void Configure()
    {
        Get("/api/service-principals");
        Definition.RequireCapability(SystemCapabilities.ManageApiKeys, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(ListRequest req, CancellationToken ct)
    {
        var tenant = PrincipalRules.TenantOf(User);
        var page = await session.Query<ServicePrincipal>()
            .Where(p => p.TenantSlug == tenant)
            .OrderByDescending(p => p.CreatedAt)
            .ToPagedResponseAsync(req, ct);

        await Send.OkAsync(new PaginatedResponse<ServicePrincipalResponse>
        {
            Items = page.Items.Select(ServicePrincipalResponse.Of).ToList(),
            Page = page.Page,
            PageSize = page.PageSize,
            TotalItems = page.TotalItems,
        }, ct);
    }
}

/// <summary>GET /api/service-principals/{id}</summary>
internal class GetServicePrincipalEndpoint(IQuerySession session) : EndpointWithoutRequest<ServicePrincipalResponse>
{
    public override void Configure()
    {
        Get("/api/service-principals/{id}");
        Definition.RequireCapability(SystemCapabilities.ManageApiKeys, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var principal = await PrincipalRules.InTenantAsync(session, Route<Guid>("id"), PrincipalRules.TenantOf(User), ct);
        if (principal is null) { await Send.NotFoundAsync(ct); return; }
        await Send.OkAsync(ServicePrincipalResponse.Of(principal), ct);
    }
}

internal sealed class SetPrincipalRolesRequest
{
    public Guid Id { get; set; }
    public List<Guid>? RoleIds { get; set; }
}

/// <summary>PUT /api/service-principals/{id}/roles replaces the principal's roles.</summary>
internal class SetServicePrincipalRolesEndpoint(IDocumentSession session, ICallerCapabilities callers)
    : Endpoint<SetPrincipalRolesRequest, ServicePrincipalResponse>
{
    public override void Configure()
    {
        Put("/api/service-principals/{id}/roles");
        Definition.RequireCapability(SystemCapabilities.ManageApiKeys, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(SetPrincipalRolesRequest req, CancellationToken ct)
    {
        var tenant = PrincipalRules.TenantOf(User);
        var principal = await PrincipalRules.InTenantAsync(session, req.Id, tenant, ct);
        if (principal is null) { await Send.NotFoundAsync(ct); return; }

        var roleIds = (req.RoleIds ?? []).Distinct().ToList();
        if (await PrincipalRules.CheckAsync(session, callers, User, roleIds, tenant, ct) is { } why)
            ThrowError(r => r.RoleIds!, why, 403);

        var before = principal.RoleIds.Select(r => r.ToString()).ToList();
        principal.RoleIds = roleIds;
        session.Store(principal);
        await AuditLog.RecordAsync(session, tenant, "service_principal.roles_changed", PrincipalRules.ActorOf(User),
            User.FindFirst("Username")?.Value, targetType: "ServicePrincipal", targetId: principal.Id.ToString(),
            metadata: new() { ["name"] = principal.Name, ["from"] = before, ["to"] = roleIds.Select(r => r.ToString()).ToList() }, ct: ct);
        if (!await PrincipalRules.SaveAsync(session, this, ct)) return;

        await Send.OkAsync(ServicePrincipalResponse.Of(principal), ct);
    }
}

/// <summary>POST /api/service-principals/{id}/disable. Its keys are refused on their next request.</summary>
internal class DisableServicePrincipalEndpoint(IDocumentSession session) : EndpointWithoutRequest<ServicePrincipalResponse>
{
    public override void Configure()
    {
        Post("/api/service-principals/{id}/disable");
        Definition.RequireCapability(SystemCapabilities.ManageApiKeys, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var tenant = PrincipalRules.TenantOf(User);
        var principal = await PrincipalRules.InTenantAsync(session, Route<Guid>("id"), tenant, ct);
        if (principal is null) { await Send.NotFoundAsync(ct); return; }

        // Disabling twice writes nothing, so the trail holds one row per disable.
        if (!principal.Disabled)
        {
            principal.DisabledAt = DateTime.UtcNow;
            session.Store(principal);
            await AuditLog.RecordAsync(session, tenant, "service_principal.disabled", PrincipalRules.ActorOf(User),
                User.FindFirst("Username")?.Value, targetType: "ServicePrincipal", targetId: principal.Id.ToString(),
                metadata: new() { ["name"] = principal.Name }, ct: ct);
            if (!await PrincipalRules.SaveAsync(session, this, ct)) return;
        }

        await Send.OkAsync(ServicePrincipalResponse.Of(principal), ct);
    }
}

internal sealed class MoveKeysRequest
{
    public Guid ServicePrincipalId { get; set; }

    /// <summary>One key to move. Leave empty and set <see cref="AllManageable"/> to move many.</summary>
    public Guid? KeyId { get; set; }

    /// <summary>
    /// Move every active key of the tenant the caller may manage: the caller's own keys, or all of
    /// them for a SuperAdmin. This is the same reach rotation has.
    /// </summary>
    public bool AllManageable { get; set; }
}

internal sealed record MoveKeysResponse(int Moved, List<Guid> KeyIds);

/// <summary>
/// POST /api/api-keys/move-to-principal. Opt-in: nothing moves unless a caller asks. A moved key
/// keeps its secret, scopes and expiry and from then on acts as the principal, so it no longer
/// depends on the person who made it.
/// </summary>
internal class MoveKeysToPrincipalEndpoint(IDocumentSession session, ICallerCapabilities callers) : Endpoint<MoveKeysRequest, MoveKeysResponse>
{
    public override void Configure()
    {
        Post("/api/api-keys/move-to-principal");
        Definition.RequireCapability(SystemCapabilities.ManageApiKeys, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(MoveKeysRequest req, CancellationToken ct)
    {
        var tenant = PrincipalRules.TenantOf(User);
        if (req.KeyId is null == !req.AllManageable)
            ThrowError("Name one key with KeyId, or set AllManageable, not both and not neither.");

        var principal = await PrincipalRules.InTenantAsync(session, req.ServicePrincipalId, tenant, ct);
        if (principal is null || principal.Disabled)
            ThrowError(r => r.ServicePrincipalId, "That service principal does not exist in this tenant or is disabled.");
        if (await PrincipalRules.NotHeldByPrincipalAsync(session, callers, User, principal!, ct) is { } notHeld)
            ThrowError(r => r.ServicePrincipalId, notHeld, 403);

        var actor = PrincipalRules.ActorOf(User);
        var superAdmin = await PlatformRoles.IsSuperAdminAsync(session, User, ct);
        var candidates = await session.Query<ApiKey>()
            .Where(k => k.TenantSlug == tenant && !k.Revoked && k.ServicePrincipalId == null
                        && (req.KeyId == null || k.Id == req.KeyId))
            .ToListAsync(ct);
        var now = DateTime.UtcNow;
        var moving = candidates
            .Where(k => k.ExpiresAt is null || k.ExpiresAt > now)
            .Where(k => superAdmin || k.UserId == actor)
            .ToList();

        if (req.KeyId is not null && moving.Count == 0)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        foreach (var key in moving)
        {
            key.ServicePrincipalId = principal!.Id;
            session.Store(key);
        }

        if (moving.Count > 0)
            await AuditLog.RecordAsync(session, tenant, "apikey.moved_to_principal", actor, User.FindFirst("Username")?.Value,
                targetType: "ServicePrincipal", targetId: principal!.Id.ToString(),
                metadata: new() { ["name"] = principal.Name, ["keyIds"] = moving.Select(k => k.Id.ToString()).ToList() }, ct: ct);
        if (!await PrincipalRules.SaveAsync(session, this, ct)) return;

        await Send.OkAsync(new MoveKeysResponse(moving.Count, moving.Select(k => k.Id).ToList()), ct);
    }
}
