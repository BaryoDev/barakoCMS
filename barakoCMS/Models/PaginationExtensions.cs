using Marten;

namespace barakoCMS.Models;

/// <summary>
/// Turns a Marten query into a page of results plus the count the envelope needs.
/// </summary>
/// <remarks>
/// Every collection endpoint returns the same envelope, and repeating skip, take and count at each
/// one is how three shapes appeared in the first place. Marten's Stats gives the unpaged total from
/// the same round trip as the page, so this is one query rather than two.
/// </remarks>
public static class PaginationExtensions
{
    public static async Task<PaginatedResponse<T>> ToPagedResponseAsync<T>(
        this IQueryable<T> query,
        PaginatedRequest request,
        CancellationToken ct)
    {
        var items = await query
            .Stats(out var stats)
            .Skip(request.Skip)
            .Take(request.Take)
            .ToListAsync(ct);

        return new PaginatedResponse<T>
        {
            Items = items.ToList(),
            Page = request.Page,
            PageSize = request.PageSize,
            TotalItems = (int)stats.TotalResults,
        };
    }

    /// <summary>
    /// Pages a collection already in memory, for the endpoints whose filtering cannot be pushed
    /// into the query.
    /// </summary>
    public static PaginatedResponse<T> ToPagedResponse<T>(
        this IReadOnlyList<T> items,
        PaginatedRequest request) =>
        new()
        {
            Items = items.Skip(request.Skip).Take(request.Take).ToList(),
            Page = request.Page,
            PageSize = request.PageSize,
            TotalItems = items.Count,
        };
}
