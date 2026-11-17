namespace barakoCMS.Models;

internal static class PagedResponseMapping
{
    /// <summary>The same page with each item mapped, keeping the paging fields.</summary>
    public static PaginatedResponse<TOut> Map<TIn, TOut>(this PaginatedResponse<TIn> page, Func<TIn, TOut> select) =>
        new()
        {
            Items = page.Items.Select(select).ToList(),
            Page = page.Page,
            PageSize = page.PageSize,
            TotalItems = page.TotalItems,
        };
}
