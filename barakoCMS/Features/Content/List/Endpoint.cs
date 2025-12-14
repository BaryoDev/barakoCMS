using FastEndpoints;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.Content.List;

public class Request
{
    public string? ContentType { get; set; }
}

public class Response : List<ContentResponse> { }

public class ContentResponse
{
    public Guid Id { get; set; }
    public string ContentType { get; set; } = string.Empty;
    public Dictionary<string, object> Data { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class Endpoint : Endpoint<Request, Response>
{
    private readonly IQuerySession _session;
    private readonly barakoCMS.Infrastructure.Services.IPermissionResolver _permissionResolver;

    public Endpoint(IQuerySession session, barakoCMS.Infrastructure.Services.IPermissionResolver permissionResolver)
    {
        _session = session;
        _permissionResolver = permissionResolver;
    }

    public override void Configure()
    {
        Get("/api/contents");
        // Removed AllowAnonymous
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        // 1. Authenticate
        var userIdClaim = User.FindFirst("System.Security.Claims.ClaimTypes.NameIdentifier") ?? User.FindFirst("UserId");
        if (userIdClaim == null || !Guid.TryParse(userIdClaim.Value, out var userId))
        {
            await SendUnauthorizedAsync(ct);
            return;
        }
        var user = await _session.LoadAsync<barakoCMS.Models.User>(userId, ct);
        if (user == null)
        {
            await SendUnauthorizedAsync(ct);
            return;
        }

        // 2. Query
        var query = _session.Query<barakoCMS.Models.Content>().AsQueryable();

        if (!string.IsNullOrEmpty(req.ContentType))
        {
            query = query.Where(c => c.ContentType == req.ContentType);
        }

        // Order by newest first
        var items = await query.OrderByDescending(c => c.CreatedAt).ToListAsync(ct);

        // 3. Filter (In-Memory Permission Check)
        // Warning: This is O(N) permission checks. Pagination is recommended for production scale.
        var allowedItems = new List<barakoCMS.Models.Content>();
        foreach (var item in items)
        {
            if (await _permissionResolver.CanPerformActionAsync(user, item.ContentType, "read", item, ct))
            {
                allowedItems.Add(item);
            }
            else
            {
                Serilog.Log.Warning($"[DEBUG] Permission Denied for Item {item.Id} ({item.ContentType})");
            }
        }
        Serilog.Log.Information($"[DEBUG] Returning {allowedItems.Count} items after filter");

        var response = new Response();
        response.AddRange(allowedItems.Select(c => new ContentResponse
        {
            Id = c.Id,
            ContentType = c.ContentType,
            Data = new Dictionary<string, object>(c.Data),
            CreatedAt = c.CreatedAt,
            UpdatedAt = c.UpdatedAt
        }));

        await SendAsync(response, cancellation: ct);
    }
}
