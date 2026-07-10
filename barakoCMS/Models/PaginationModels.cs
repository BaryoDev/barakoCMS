namespace barakoCMS.Models;

/// <summary>
/// Base class for paginated requests.
/// Enforces maximum page size of 100 items.
/// </summary>
public class PaginatedRequest
{
    private int _pageSize = 20;
    private int _page = 1;

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
        set => _pageSize = value < 1 ? 1 : Math.Min(value, 100);
    }
    
    /// <summary>
    /// Column to sort by (optional)
    /// </summary>
    public string? SortBy { get; set; }
    
    /// <summary>
    /// Sort order: "asc" or "desc"
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
