using barakoCMS.Core.Interfaces;
using FastEndpoints;
using Marten;
using Microsoft.Extensions.Options;

namespace BarakoCMS.Pages.Features.Resolve;

internal sealed record Breadcrumb(Guid Id, string? Title, string Slug, string Path);

internal sealed record ResolveResponse(
    int Contract,
    string Path,
    PublicContentProjection Entry,
    IReadOnlyList<Breadcrumb> Breadcrumbs);

/// <summary>
/// GET /api/public/pages/resolve?path=/about/team: the published page at a path, with its canonical
/// path and breadcrumbs from the root down to itself. 404 when nothing is served there.
/// </summary>
/// <remarks>
/// The core slug route cannot tell /about/team from /careers/team. This looks up the pages holding the
/// last segment's slug, walks each one's parents, and answers with the one whose whole path matches.
/// Every page on the walk has to be published and Public, so a published page under a draft does not
/// resolve. The walk reads at most <see cref="PagesOptions.MaxDepth"/> ancestors per candidate.
/// </remarks>
internal sealed class Endpoint : EndpointWithoutRequest<ResolveResponse>
{
    private const int MaxPathLength = 2048;

    private readonly IQuerySession _session;
    private readonly IPublicContentProjector _projector;
    private readonly PagesOptions _options;

    public Endpoint(IQuerySession session, IPublicContentProjector projector, IOptions<PagesOptions> options)
    {
        _session = session;
        _projector = projector;
        _options = options.Value;
    }

    public override void Configure()
    {
        Get("/api/public/pages/resolve");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var raw = Query<string>("path", isRequired: false) ?? "/";
        var segments = raw.Length > MaxPathLength
            ? null
            : raw.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var definition = await PublishedPages.DefinitionAsync(_session, _options.ContentType, ct);
        if (segments is null || segments.Length > _options.MaxDepth + 1 || !_projector.IsDeliverable(definition))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var slug = segments.Length == 0 ? _options.HomeSlug : segments[^1];
        if (string.IsNullOrWhiteSpace(slug))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var requested = "/" + string.Join('/', segments);
        var candidates = await PublishedPages.WithSlugAsync(_session, _projector, definition!, _options, slug, ct);

        foreach (var (node, entry) in candidates)
        {
            var nodes = new List<PageNode> { node };
            var parent = node.ParentId;
            for (var depth = 0; parent is { } parentId && depth < _options.MaxDepth; depth++)
            {
                if (await PublishedPages.ByIdAsync(_session, _projector, definition!, _options, parentId, ct) is not { } ancestor)
                {
                    break;
                }

                nodes.Add(ancestor.Node);
                parent = ancestor.Node.ParentId;
            }

            var tree = new PageTree(nodes, _options.HomeSlug, _options.MaxDepth);
            if (tree.Ancestry(node.Id) is not { } chain)
            {
                continue;
            }

            var path = tree.PathOf(chain);
            if (!string.Equals(path, requested, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var breadcrumbs = chain
                .Select((n, i) => new Breadcrumb(n.Id, n.Title, n.Slug!, tree.PathOf(chain.Take(i + 1).ToList())))
                .ToList();

            PublishedPages.SetCache(HttpContext);
            await Send.OkAsync(new ResolveResponse(PagesContract.Version, path, entry, breadcrumbs), ct);
            return;
        }

        await Send.NotFoundAsync(ct);
    }
}
