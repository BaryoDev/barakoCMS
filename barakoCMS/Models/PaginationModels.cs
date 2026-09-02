namespace barakoCMS.Models;

/// <summary>
/// Base class for paginated requests.
/// Enforces maximum page size of 100 items.
/// </summary>
public class PaginatedRequest
{
    /// <summary>
    /// The largest page any endpoint will return, whatever the caller asks for.
    /// </summary>
    public const int MaxPageSize = 100;

    private int _pageSize;
    private int _page = 1;

    public PaginatedRequest()
        : this(defaultPageSize: 20)
    {
    }

    protected PaginatedRequest(int defaultPageSize)
    {
        _pageSize = defaultPageSize;
    }

    /// <summary>
    /// Page number (1-indexed). Values below 1 are clamped to 1 to prevent negative OFFSET.
    /// </summary>
    public int Page
    {
        get => _page;
        set => _page = value < 1 ? 1 : value;
    }

    /// <summary>
    /// Number of items per page. Clamped to the range 1..100 to prevent negative/zero LIMIT.
    /// </summary>
    public int PageSize
    {
        get => _pageSize;
        set => _pageSize = value < 1 ? 1 : Math.Min(value, MaxPageSize);
    }
    
    // SortBy was here, accepted on every paginated endpoint, documented in Swagger, and honoured by
    // none of them: a repo-wide search matched only its own declaration. On /api/public/{type} it
    // was worse than useless, because that endpoint deliberately 400s on ?sort= with a comment
    // saying accepting-and-ignoring "would be a silent wrong answer", while ?sortBy= was skipped as
    // an unknown key and returned exactly that. Removed rather than implemented: a parameter that
    // lies is worse in a frozen spec than one that is missing, and 4.0 is the last chance to drop
    // it. Sorting can come back as a real feature, additively, whenever someone needs it.

    /// <summary>
    /// Sort order: "asc" or "desc". Honoured by the endpoints that document a sort column.
    /// </summary>
    public string SortOrder { get; set; } = "desc";
    
    /// <summary>
    /// Number of items to skip (for database query)
    /// </summary>
    public int Skip => (Page - 1) * PageSize;
    
    /// <summary>
    /// Number of items to take (for database query)
    /// </summary>
    public int Take => PageSize;
}

/// <summary>
/// Generic paginated response with metadata.
/// </summary>
/// <typeparam name="T">Type of items in the response</typeparam>
public class PaginatedResponse<T>
{
    /// <summary>
    /// Items for the current page
    /// </summary>
    public IReadOnlyList<T> Items { get; set; } = Array.Empty<T>();
    
    /// <summary>
    /// Current page number (1-indexed)
    /// </summary>
    public int Page { get; set; }
    
    /// <summary>
    /// Number of items per page
    /// </summary>
    public int PageSize { get; set; }
    
    /// <summary>
    /// Total number of items across all pages
    /// </summary>
    public int TotalItems { get; set; }
    
    /// <summary>
    /// Total number of pages
    /// </summary>
    public int TotalPages => PageSize > 0 ? (int)Math.Ceiling(TotalItems / (double)PageSize) : 0;
    
    /// <summary>
    /// Whether there is a next page
    /// </summary>
    public bool HasNextPage => Page < TotalPages;
    
    /// <summary>
    /// Whether there is a previous page
    /// </summary>
    public bool HasPreviousPage => Page > 1;
}

/// <summary>
/// A paginated request for a collection that used to come back as a bare unbounded array.
/// </summary>
/// <remarks>
/// These are administrative lists: content types, tenants, API keys, workflows, user groups,
/// devices. They were unbounded, so any default below the maximum would silently truncate an
/// existing caller. Starting at the cap means a deployment small enough not to have noticed the
/// difference still does not, while a large one can page instead of pulling the whole table.
/// </remarks>
public class ListRequest : PaginatedRequest
{
    public ListRequest()
        : base(defaultPageSize: MaxPageSize)
    {
    }
}
