using System.Text.Json;
using barakoCMS.Infrastructure.Caching;

namespace barakoCMS.Features.WorkflowRuns;

/// <summary>
/// A weak ETag for a page of <c>GET /api/workflow-runs</c>, so a poll that finds nothing changed
/// answers 304 with no body.
/// </summary>
/// <remarks>
/// Hashes the runs, the page and the counts, and leaves out the cursor and the retention times:
/// both are taken from the clock, so including them would make every response differ from the last.
/// A cursor the client keeps from an earlier response stays valid, so skipping the new one loses nothing.
/// </remarks>
internal static class RunListETag
{
    public static string Compute(
        HttpContext http,
        IReadOnlyList<RunResponse> items,
        int page,
        int pageSize,
        int totalItems,
        Dictionary<string, int>? counts)
    {
        var material = JsonSerializer.Serialize(new
        {
            items,
            page,
            pageSize,
            totalItems,
            counts = counts?.OrderBy(c => c.Key, StringComparer.Ordinal).ToList(),
        });

        return DeliveryCache.WeakETag(DeliveryCache.TenantOf(http), material);
    }
}
