using FastEndpoints;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.Content.List;

internal class Request : PaginatedRequest
{
    public string? ContentType { get; set; }
}

internal class ContentResponse
{
    public Guid Id { get; set; }
    public string ContentType { get; set; } = string.Empty;
    public Dictionary<string, object> Data { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    // The single-item GET returns these and the list did not, so an entries table had no way to
    // show whether a row was a Draft. Adding a field to a response is not a breaking change, and
    // the alternative was every list row costing a second request to find out.
    public barakoCMS.Models.ContentStatus Status { get; set; }
    public barakoCMS.Models.SensitivityLevel Sensitivity { get; set; }
}

internal class Endpoint : Endpoint<Request, PaginatedResponse<ContentResponse>>
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
        // "UserId" is the only identity claim the token carries. This used to look first for the
        // literal string System.Security.Claims.ClaimTypes.NameIdentifier, which is the name of a
        // constant and not its value, so it never matched and the fallback was always what ran.
        var userIdClaim = User.FindFirst("UserId");
        if (userIdClaim == null || !Guid.TryParse(userIdClaim.Value, out var userId))
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }
        
        var user = await _session.LoadAsync<barakoCMS.Models.User>(userId, ct);
        if (user == null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        // 2. Build Query
        var query = _session.Query<barakoCMS.Models.Content>().AsQueryable();

        if (!string.IsNullOrEmpty(req.ContentType))
        {
            query = query.Where(c => c.ContentType == req.ContentType);
        }

        // 3. Apply Sorting
        query = req.SortOrder.ToLower() == "asc"
            ? query.OrderBy(c => c.CreatedAt)
            : query.OrderByDescending(c => c.CreatedAt);

        // 4. Load every row matching the content-type filter, in order. Permission can be
        // conditional on the specific item (PermissionResolver.CanPerformActionAsync's `content`
        // param), so there is no cheaper query-level filter that is safe to apply before this check —
        // a per-content-type check with no item would grant access based on rules that are only
        // supposed to hold for SOME items of that type. Pagination has to run over the permitted set,
        // not the raw one, or a restricted user's page boundaries and total count are both wrong.
        var allMatching = await query.ToListAsync(ct);

        // 5. Filter by Permission over the WHOLE matching set (not just one page of it), so a run of
        // denied items can never produce a short or empty page while permitted items exist further
        // down the raw ordering.
        var sensitivity = Resolve<barakoCMS.Core.Interfaces.ISensitivityService>();
        var permittedItems = new List<ContentResponse>();
        foreach (var item in allMatching)
        {
            if (await _permissionResolver.CanPerformActionAsync(user, item.ContentType, "read", item, ct))
            {
                var response = new ContentResponse
                {
                    Id = item.Id,
                    ContentType = item.ContentType,
                    Data = new Dictionary<string, object>(item.Data),
                    CreatedAt = item.CreatedAt,
                    UpdatedAt = item.UpdatedAt,
                    Status = item.Status,
                    Sensitivity = item.Sensitivity
                };
                // Same document- and field-level scrubbing as Get, so lists never leak sensitive data.
                if (await sensitivity.ApplyAsync(item.ContentType, item.Sensitivity, response.Data, HttpContext, ct))
                    response.ContentType = "HIDDEN";
                permittedItems.Add(response);
            }
        }

        _logger.LogInformation(
            "Permission filtering: Retrieved={Retrieved}, Permitted={Permitted}",
            allMatching.Count, permittedItems.Count);

        // 6. Paginate the PERMITTED set (order is already applied and preserved from step 3).
        var pagedItems = permittedItems.Skip(req.Skip).Take(req.Take).ToList();

        _logger.LogInformation(
            "Content list query: Page={Page}, PageSize={PageSize}, VisibleTotal={VisibleTotal}, Returned={Returned}",
            req.Page, req.PageSize, permittedItems.Count, pagedItems.Count);

        // 7. Return Paginated Response
        await Send.ResponseAsync(new PaginatedResponse<ContentResponse>
        {
            Items = pagedItems,
            Page = req.Page,
            PageSize = req.PageSize,
            TotalItems = permittedItems.Count // Honest: counts only what this user can see.
        }, cancellation: ct);
    }
}
