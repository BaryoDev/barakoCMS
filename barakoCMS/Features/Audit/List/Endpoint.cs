using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.Audit.List;

internal class ListRequest : PaginatedRequest
{
    /// <summary>Filter to one actor. Null = any.</summary>
    public Guid? ActorUserId { get; set; }

    /// <summary>Filter by exact action name (e.g. "auth.login.failed"). Null = any.</summary>
    public string? Action { get; set; }

    /// <summary>Only entries at or after this instant (UTC). Null = no lower bound.</summary>
    public DateTime? From { get; set; }

    /// <summary>Only entries at or before this instant (UTC). Null = no upper bound.</summary>
    public DateTime? To { get; set; }

    /// <summary>Filter to one tenant slug. Null = every tenant.</summary>
    public string? Tenant { get; set; }
}

internal class AuditEventDto
{
    public Guid Id { get; set; }
    public string TenantSlug { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public Guid? ActorUserId { get; set; }
    public string? ActorUsername { get; set; }
    public string? TargetType { get; set; }
    public string? TargetId { get; set; }
    public Dictionary<string, object>? Metadata { get; set; }
    public string? IpAddress { get; set; }
    public DateTime CreatedAt { get; set; }

    /// <summary>The metadata keys of a role row shown to a caller who cannot list roles.</summary>
    private static readonly string[] RoleSummary = ["name", "nameBefore"];

    /// <summary>The metadata keys of an API key row shown to a caller who cannot list keys.</summary>
    private static readonly string[] KeySummary = ["name"];

    /// <summary>
    /// The stored row is complete. What is returned is no more than the caller could read from the
    /// route that owns the data.
    /// </summary>
    /// <remarks>
    /// <c>view_audit_log</c> is held by people who hold neither <c>manage_roles</c> nor
    /// <c>manage_api_keys</c>, and a role is a global document. A role row written while a platform
    /// administrator was resolved to one tenant would otherwise show that tenant's administrator
    /// the capabilities of platform roles and the content types of other tenants named in the
    /// role's permissions. Such a caller still sees that the change happened, who made it and the
    /// name of what it was made to.
    /// </remarks>
    internal static AuditEventDto VisibleTo(AuditEvent e, bool mayListRoles, bool mayListKeys)
    {
        var dto = From(e);

        if (!mayListRoles && e.Action.StartsWith("role.", StringComparison.Ordinal))
            dto.Metadata = Only(e.Metadata, RoleSummary);
        else if (!mayListKeys && e.Action.StartsWith("apikey.", StringComparison.Ordinal))
            dto.Metadata = Only(e.Metadata, KeySummary);

        return dto;
    }

    private static Dictionary<string, object>? Only(Dictionary<string, object>? metadata, string[] keys) =>
        metadata?.Where(kv => keys.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);

    internal static AuditEventDto From(AuditEvent e) => new()
    {
        Id = e.Id,
        TenantSlug = e.TenantSlug,
        Action = e.Action,
        ActorUserId = e.ActorUserId,
        ActorUsername = e.ActorUsername,
        TargetType = e.TargetType,
        TargetId = e.TargetId,
        Metadata = e.Metadata,
        IpAddress = e.IpAddress,
        CreatedAt = e.CreatedAt,
    };
}

/// <summary>GET /api/audit — browse the audit trail, newest first.</summary>
internal class Endpoint(
    IQuerySession session,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant) : Endpoint<ListRequest, PaginatedResponse<AuditEventDto>>
{
    public override void Configure()
    {
        Get("/api/audit");
        Definition.RequireCapability(SystemCapabilities.ViewAuditLog, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(ListRequest req, CancellationToken ct)
    {
        var query = session.Query<AuditEvent>().AsQueryable();

        // AuditEvent is SingleTenanted (one global table), so the conjoined session provides no
        // tenant isolation here; the tenant boundary is the caller's to prove, not the session's.
        // A tenant admin sees only their own tenant's trail. Only a SuperAdmin, whose reach is
        // global, may read across tenants and narrow with ?tenant=. Without this, any tenant admin
        // reads every tenant's audit log by leaving ?tenant unset.
        Guid.TryParse(User.FindFirst("UserId")?.Value, out var callerId);
        var caller = await session.LoadAsync<User>(callerId, ct);
        var isSuperAdmin = caller?.RoleIds.Contains(SystemRoles.SuperAdminRoleId) == true;
        if (isSuperAdmin)
        {
            if (!string.IsNullOrWhiteSpace(req.Tenant))
                query = query.Where(e => e.TenantSlug == req.Tenant);
        }
        else
        {
            query = query.Where(e => e.TenantSlug == tenant.Slug);
        }

        if (req.ActorUserId is Guid actorId)
            query = query.Where(e => e.ActorUserId == actorId);
        if (!string.IsNullOrWhiteSpace(req.Action))
            query = query.Where(e => e.Action == req.Action);
        if (req.From is { } from) { var bound = AsUtc(from); query = query.Where(e => e.CreatedAt >= bound); }
        if (req.To is { } to) { var bound = AsUtc(to); query = query.Where(e => e.CreatedAt <= bound); }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(e => e.CreatedAt)
            .Skip(req.Skip).Take(req.Take)
            .ToListAsync(ct);

        // The gates are the ones GET /api/roles and GET /api/api-keys declare, asked by the rule
        // the gate itself uses, and only when the page holds a row the answer changes.
        var mayListRoles = items.Any(e => e.Action.StartsWith("role.", StringComparison.Ordinal))
            && await CapabilityGateProcessor.HoldsAsync(HttpContext, barakoCMS.Features.Roles.List.Endpoint.Gate, ct);
        var mayListKeys = items.Any(e => e.Action.StartsWith("apikey.", StringComparison.Ordinal))
            && await CapabilityGateProcessor.HoldsAsync(HttpContext, barakoCMS.Features.ApiKeys.ListApiKeysEndpoint.Gate, ct);

        await Send.ResponseAsync(new PaginatedResponse<AuditEventDto>
        {
            Items = items.Select(e => AuditEventDto.VisibleTo(e, mayListRoles, mayListKeys)).ToList(),
            Page = req.Page,
            PageSize = req.PageSize,
            TotalItems = total,
        }, cancellation: ct);
    }

    /// <summary>
    /// A query-string timestamp with an offset binds as local time, and CreatedAt is UTC, so on any
    /// host not running in UTC the window would be shifted by the zone. One without an offset is
    /// taken as UTC, which is what ListRequest.From and ListRequest.To already document.
    /// </summary>
    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Local => value.ToUniversalTime(),
        DateTimeKind.Utc => value,
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
