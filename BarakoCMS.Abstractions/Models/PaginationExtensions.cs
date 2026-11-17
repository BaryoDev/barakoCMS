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
        // Stats marks the query it is called on and a count from that query is refused, so the
        // fallback count comes from a copy made before Stats. It only runs for an empty page.
        var uncounted = query.Provider.CreateQuery<T>(query.Expression);

        var items = await query
            .Stats(out var stats)
            .Skip(request.Skip)
            .Take(request.Take)
            .ToListAsync(ct);

        // A page past the end has no row to carry the total, so Stats reports 0. Count it on its own.
        var total = items.Count == 0 && request.Skip > 0
            ? await uncounted.CountAsync(ct)
            : (int)stats.TotalResults;

        return new PaginatedResponse<T>
        {
            Items = items,
            Page = request.Page,
            PageSize = request.PageSize,
            TotalItems = total,
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
