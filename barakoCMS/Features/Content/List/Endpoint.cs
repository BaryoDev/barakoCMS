using FastEndpoints;
using Marten;
using Marten.Linq.MatchesSql;
using barakoCMS.Models;

namespace barakoCMS.Features.Content.List;

internal class Request : PaginatedRequest
{
    public string? ContentType { get; set; }

    /// <summary>Matches any string value in an entry's data, case-insensitively.</summary>
    /// <remarks>
    /// Every value, not the derived <c>SearchText</c> the anonymous delivery search uses. That one
    /// holds only the values of fields the type declares Public, which is correct for a caller who
    /// may see nothing else and wrong for an administrator who may. Searching it here would mean an
    /// admin typing a customer's reference number, which happens to sit in a Sensitive field, and
    /// getting nothing back with no way to tell a missing entry from a hidden one.
    ///
    /// Matching more than the caller may read is safe because the per-item permission check and the
    /// sensitivity scrub both run afterwards on whatever this returns. The visibility rules stay in
    /// one place rather than being restated as a query filter that could drift from them.
    ///
    /// Field names are not matched, only values. Searching "title" should not return every entry of
    /// every type that has a Title.
    /// </remarks>
    public string? Search { get; set; }

    /// <summary>One status, or null for every status.</summary>
    public barakoCMS.Models.ContentStatus? Status { get; set; }
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

    /// <summary>The stream version, the same number the single-item GET returns.</summary>
    /// <remarks>
    /// Read in one batched query for the page rather than one call per row. The version is not on
    /// the Content document (the document is the fold, the version belongs to the stream), so it
    /// costs a round trip either way; making it one for the page rather than one for each of up to a
    /// hundred rows is the difference between a column and a reason not to have the column.
    ///
    /// Zero for an entry with no stream behind it, which is seeded demo data and anything written
    /// before every write went through the writer.
    /// </remarks>
    public long Version { get; set; }
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

        // Status and search are pushed into the query, and that is safe in a way a permission filter
        // would not be. Both can only remove rows. A caller asking for Drafts is asking for a subset
        // of what they may read, so narrowing first and checking permission after gives the same
        // answer as checking everything and discarding; a filter that GRANTED would not, which is
        // why the permission check below stays where it is.
        if (req.Status is { } status)
        {
            query = query.Where(c => c.Status == status);
        }

        var term = req.Search?.Trim();
        if (!string.IsNullOrEmpty(term))
        {
            // ILIKE over every value in the entry's data. jsonb_each_text flattens one level, so a
            // nested object is compared as its own JSON text: a substring still matches, which is
            // more useful than skipping it and is the honest description of what this does.
            //
            // The term is a bound parameter. The escaping below is not about injection, it is about
            // meaning: an unescaped % or _ is a wildcard, so searching for "50%" would match every
            // entry containing "50" and searching for "a_b" would match "axb".
            query = query.Where(c => c.MatchesSql(SearchSql, EscapeLike(term)));
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

        await FillVersionsAsync(pagedItems, ct);

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

    /// <summary>
    /// True when any value in the entry's data contains the term, ignoring case.
    /// </summary>
    /// <remarks>
    /// <c>d</c> is the document alias Marten gives the table inside <c>MatchesSql</c>, and <c>?</c>
    /// is its parameter placeholder. Both match how the public delivery filters are built next door
    /// in <c>DeliveryQuery</c>, deliberately, so there is one shape of hand-written jsonb predicate
    /// in this codebase rather than two.
    /// </remarks>
    private const string SearchSql =
        "EXISTS (SELECT 1 FROM jsonb_each_text(d.data -> 'Data') kv WHERE kv.value ILIKE '%' || ? || '%')";

    /// <summary>Neutralises the two LIKE wildcards so a search means what was typed.</summary>
    private static string EscapeLike(string term) => term
        .Replace("\\", "\\\\")
        .Replace("%", "\\%")
        .Replace("_", "\\_");

    /// <summary>
    /// Reads the stream version for one page of rows, in a single round trip.
    /// </summary>
    /// <remarks>
    /// After paging rather than before, so this costs one query for at most a page of rows rather
    /// than one for every entry the caller can read. Marten's batched query issues them together.
    /// </remarks>
    private async Task FillVersionsAsync(IReadOnlyList<ContentResponse> items, CancellationToken ct)
    {
        if (items.Count == 0) return;

        var batch = _session.CreateBatchQuery();
        var states = items.Select(i => batch.Events.FetchStreamState(i.Id)).ToList();

        await batch.Execute(ct);

        for (var i = 0; i < items.Count; i++)
        {
            // Null for an entry with no stream, which stays 0 rather than throwing. Seeded rows are
            // like this, and a list that fell over on one of them would be worse than a zero.
            items[i].Version = (await states[i])?.Version ?? 0;
        }
    }
}
