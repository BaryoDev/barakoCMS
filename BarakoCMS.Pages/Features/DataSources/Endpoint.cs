using barakoCMS.Core.Interfaces;
using FastEndpoints;

namespace BarakoCMS.Pages.Features.DataSources;

internal sealed record DataSourceItem(string Id, BlockDataSourceShape Shape, string? Capability);

internal sealed record DataSourcesResponse(int Contract, IReadOnlyList<DataSourceItem> Items);

/// <summary>
/// GET /api/pages/data-sources: the read-only data sources modules registered for page blocks to
/// bind to, ordered by id.
/// </summary>
/// <remarks>
/// Any signed-in caller. A source that names a capability is left out for a caller who does not
/// hold it, as the incidents view does. Sources are registered by the deployment's modules, not
/// stored per tenant, so the list differs between tenants only through what the caller's roles hold.
/// </remarks>
internal sealed class Endpoint(BlockDataSources sources, ICallerCapabilities capabilities)
    : EndpointWithoutRequest<DataSourcesResponse>
{
    public override void Configure()
    {
        Get("/api/pages/data-sources");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var items = new List<DataSourceItem>();
        foreach (var source in sources.All)
        {
            if (source.Capability is { } needed && !await capabilities.HoldsAsync(User, needed, ct))
            {
                continue;
            }

            items.Add(new DataSourceItem(source.Id, source.Shape, source.Capability));
        }

        await Send.OkAsync(new DataSourcesResponse(PagesContract.Version, items), ct);
    }
}
