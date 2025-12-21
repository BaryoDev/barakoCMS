using FastEndpoints;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.Content.List;

public class Request : PaginatedRequest
{
    public string? ContentType { get; set; }
}

public class ContentResponse
{
    public Guid Id { get; set; }
    public string ContentType { get; set; } = string.Empty;
    public Dictionary<string, object> Data { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class Endpoint : Endpoint<Request, PaginatedResponse<ContentResponse>>
{
    private readonly IQuerySession _session;
    private readonly barakoCMS.Infrastructure.Services.IPermissionResolver _permissionResolver;
    private readonly ILogger<Endpoint> _logger;

    public Endpoint(
        IQuerySession session,
        barakoCMS.Infrastructure.Services.IPermissionResolver permissionResolver,
        ILogger<Endpoint> logger)
    {
        _session = session;
        _permissionResolver = permissionResolver;
        _logger = logger;
    }

    public override void Configure()
    {
        Get("/api/contents");
        // Removed AllowAnonymous - requires authentication
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

        // 2. Build Query
        var query = _session.Query<barakoCMS.Models.Content>().AsQueryable();

        if (!string.IsNullOrEmpty(req.ContentType))
        {
            query = query.Where(c => c.ContentType == req.ContentType);
        }

        // 3. Get Total Count (before pagination)
        var totalCount = await query.CountAsync(ct);

        // 4. Apply Sorting
        query = req.SortOrder.ToLower() == "asc"
            ? query.OrderBy(c => c.CreatedAt)
            : query.OrderByDescending(c => c.CreatedAt);

        // 5. Apply Pagination (CRITICAL: Limits result set size)
        var items = await query
            .Skip(req.Skip)
            .Take(req.Take)
            .ToListAsync(ct);

        _logger.LogInformation(
            "Content list query: Page={Page}, PageSize={PageSize}, TotalCount={TotalCount}, Retrieved={Retrieved}",
            req.Page, req.PageSize, totalCount, items.Count);

        // 6. Filter by Permission (now O(pageSize) not O(total))
        // This is much more efficient - only checking permissions for items on current page
        var permittedItems = new List<ContentResponse>();
        foreach (var item in items)
        {
            if (await _permissionResolver.CanPerformActionAsync(user, item.ContentType, "read", item, ct))
            {
                permittedItems.Add(new ContentResponse
                {
                    Id = item.Id,
                    ContentType = item.ContentType,
                    Data = new Dictionary<string, object>(item.Data),
                    CreatedAt = item.CreatedAt,
                    UpdatedAt = item.UpdatedAt
                });
            }
        }

        _logger.LogInformation(
            "Permission filtering: Retrieved={Retrieved}, Permitted={Permitted}",
            items.Count, permittedItems.Count);

        // 7. Return Paginated Response
        await SendAsync(new PaginatedResponse<ContentResponse>
        {
            Items = permittedItems,
            Page = req.Page,
            PageSize = req.PageSize,
            TotalItems = totalCount // Note: This is total before permission filtering
        }, cancellation: ct);
    }
}
