using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace BarakoCMS.Diagnostics.Features.List;

public class ListRequest : PaginatedRequest
{
    /// <summary>Filter by resolved state. Null = both.</summary>
    public bool? Resolved { get; set; }

    /// <summary>Filter by "error" or "warning". Null = both.</summary>
    public string? Severity { get; set; }

    /// <summary>Free-text match on the message.</summary>
    public string? Q { get; set; }
}

public class ClientErrorDto
{
    public Guid Id { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? Stack { get; set; }
    public string? Source { get; set; }
    public int? Status { get; set; }
    public string? Url { get; set; }
    public string? UserAgent { get; set; }
    public string? AppVersion { get; set; }
    public string? Tenant { get; set; }
    public string? Username { get; set; }
    public int Count { get; set; }
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public bool Resolved { get; set; }

    internal static ClientErrorDto From(ClientError e) => new()
    {
        Id = e.Id,
        Kind = e.Kind,
        Severity = e.Severity,
        Message = e.Message,
        Stack = e.Stack,
        Source = e.Source,
        Status = e.Status,
        Url = e.Url,
        UserAgent = e.UserAgent,
        AppVersion = e.AppVersion,
        Tenant = e.Tenant,
        Username = e.Username,
        Count = e.Count,
        FirstSeenAt = e.FirstSeenAt,
        LastSeenAt = e.LastSeenAt,
        Resolved = e.Resolved,
    };
}

/// <summary>GET /api/client-errors — browse captured errors, newest activity first.</summary>
public class Endpoint : Endpoint<ListRequest, PaginatedResponse<ClientErrorDto>>
{
    private readonly IQuerySession _session;
    public Endpoint(IQuerySession session) => _session = session;

    public override void Configure()
    {
        Get("/api/client-errors");
        Roles("SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(ListRequest req, CancellationToken ct)
    {
        var query = _session.Query<ClientError>().AsQueryable();

        if (req.Resolved is bool resolved)
            query = query.Where(e => e.Resolved == resolved);
        if (!string.IsNullOrWhiteSpace(req.Severity))
            query = query.Where(e => e.Severity == req.Severity);
        if (!string.IsNullOrWhiteSpace(req.Q))
            query = query.Where(e => e.Message.Contains(req.Q!, StringComparison.OrdinalIgnoreCase));

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(e => e.LastSeenAt)
            .Skip(req.Skip).Take(req.Take)
            .ToListAsync(ct);

        await SendAsync(new PaginatedResponse<ClientErrorDto>
        {
            Items = items.Select(ClientErrorDto.From).ToList(),
            Page = req.Page,
            PageSize = req.PageSize,
            TotalItems = total,
        }, cancellation: ct);
    }
}
