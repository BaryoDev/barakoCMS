using System.Text.Json;
using barakoCMS.Infrastructure.Caching;

namespace barakoCMS.Features.WorkflowRuns;

/// <summary>
/// A weak ETag for a page of <c>GET /api/workflow-runs</c>, so a poll that finds nothing changed
/// answers 304 with no body.
/// </summary>
/// <remarks>
/// Hashes the runs, the page, the counts and the configured retention windows. The cursor and the
/// cutoff dates derived from them are left out: they come from the clock, so including them would
/// make every response differ from the last. A changed window changes the tag.
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
        Dictionary<string, int>? counts,
        string retention)
    {
        var material = JsonSerializer.Serialize(new
        {
            items,
            page,
            pageSize,
            totalItems,
            retention,
            counts = counts?.OrderBy(c => c.Key, StringComparer.Ordinal).ToList(),
        });

        return DeliveryCache.WeakETag(DeliveryCache.TenantOf(http), material);
    }
}
