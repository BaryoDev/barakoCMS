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
internal class Endpoint : Endpoint<ListRequest, PaginatedResponse<AuditEventDto>>
{
    private readonly IQuerySession _session;
    public Endpoint(IQuerySession session) => _session = session;

    public override void Configure()
    {
        Get("/api/audit");
        Definition.RequireCapability(SystemCapabilities.ViewAuditLog, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(ListRequest req, CancellationToken ct)
    {
        var query = _session.Query<AuditEvent>().AsQueryable();

        if (req.ActorUserId is Guid actorId)
            query = query.Where(e => e.ActorUserId == actorId);
        if (!string.IsNullOrWhiteSpace(req.Action))
            query = query.Where(e => e.Action == req.Action);
        if (req.From is DateTime from)
            query = query.Where(e => e.CreatedAt >= from);
        if (req.To is DateTime to)
            query = query.Where(e => e.CreatedAt <= to);
        if (!string.IsNullOrWhiteSpace(req.Tenant))
            query = query.Where(e => e.TenantSlug == req.Tenant);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(e => e.CreatedAt)
            .Skip(req.Skip).Take(req.Take)
            .ToListAsync(ct);

        await Send.ResponseAsync(new PaginatedResponse<AuditEventDto>
        {
            Items = items.Select(AuditEventDto.From).ToList(),
            Page = req.Page,
            PageSize = req.PageSize,
            TotalItems = total,
        }, cancellation: ct);
    }
}
