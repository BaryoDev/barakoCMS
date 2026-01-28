using FastEndpoints;
using Marten;
using barakoCMS.Models;
using barakoCMS.Core.Interfaces;

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
    public SensitivityLevel Sensitivity { get; set; }
}

public class Endpoint : Endpoint<Request, PaginatedResponse<ContentResponse>>
{
    private readonly IQuerySession _session;
    private readonly barakoCMS.Infrastructure.Services.IPermissionResolver _permissionResolver;
    private readonly ISensitivityService _sensitivityService;
    private readonly ILogger<Endpoint> _logger;

    public Endpoint(
        IQuerySession session,
        barakoCMS.Infrastructure.Services.IPermissionResolver permissionResolver,
        ISensitivityService sensitivityService,
        ILogger<Endpoint> logger)
    {
        _session = session;
        _permissionResolver = permissionResolver;
        _sensitivityService = sensitivityService;
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

        // 6. Load content type definitions for field-level sensitivity (cached by content type)
        var contentTypeCache = new Dictionary<string, ContentTypeDefinition?>();

        // 7. Filter by Permission and apply sensitivity (now O(pageSize) not O(total))
        var permittedItems = new List<ContentResponse>();
        foreach (var item in items)
        {
            if (await _permissionResolver.CanPerformActionAsync(user, item.ContentType, "read", item, ct))
            {
                // Get or load content type definition
                if (!contentTypeCache.TryGetValue(item.ContentType, out var contentTypeDef))
                {
                    contentTypeDef = await _session
                        .Query<ContentTypeDefinition>()
                        .FirstOrDefaultAsync(x => x.Name == item.ContentType, ct);
                    contentTypeCache[item.ContentType] = contentTypeDef;
                }

                // Create response with copy of data
                var responseData = new Dictionary<string, object>(item.Data);

                // Apply field-level sensitivity filtering
                _sensitivityService.ApplyFieldSensitivity(responseData, HttpContext, contentTypeDef);

                permittedItems.Add(new ContentResponse
                {
                    Id = item.Id,
                    ContentType = item.ContentType,
                    Data = responseData,
                    CreatedAt = item.CreatedAt,
                    UpdatedAt = item.UpdatedAt,
                    Sensitivity = item.Sensitivity
                });
            }
        }

        _logger.LogInformation(
            "Permission filtering: Retrieved={Retrieved}, Permitted={Permitted}",
            items.Count, permittedItems.Count);

        // 8. Return Paginated Response
        await SendAsync(new PaginatedResponse<ContentResponse>
        {
            Items = permittedItems,
            Page = req.Page,
            PageSize = req.PageSize,
            TotalItems = totalCount // Note: This is total before permission filtering
        }, cancellation: ct);
    }
}
