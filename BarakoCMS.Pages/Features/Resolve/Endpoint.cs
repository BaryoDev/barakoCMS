using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Viewers;
using FastEndpoints;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace BarakoCMS.Pages.Features.Resolve;

internal sealed record Breadcrumb(Guid Id, string? Title, string Slug, string Path);

// BoundBlocks: the entries each block of the page that names a binding selects, empty for a page
// with none.
internal sealed record ResolveResponse(
    int Contract,
    string Path,
    PublicContentProjection Entry,
    IReadOnlyList<Breadcrumb> Breadcrumbs,
    IReadOnlyList<BoundBlock> BoundBlocks);

/// <summary>
/// GET /api/public/pages/resolve?path=/about/team: the published page at a path, with its canonical
/// path and breadcrumbs from the root down to itself. 404 when nothing is served there.
/// </summary>
/// <remarks>
/// The core slug route cannot tell /about/team from /careers/team. This looks up the pages holding the
/// last segment's slug, walks each one's parents, and answers with the one whose whole path matches.
/// Every page on the walk has to be published and Public, so a published page under a draft does not
/// resolve. The walk reads at most <see cref="PagesOptions.MaxDepth"/> ancestors per candidate.
/// A request carrying a valid X-Viewer-Token for the tenant also resolves Members pages, and the
/// answer is then private and not stored. A token that does not verify answers 401, as on the core routes.
/// </remarks>
internal sealed class Endpoint : EndpointWithoutRequest<ResolveResponse>
{
    private const int MaxPathLength = 2048;

    private readonly IQuerySession _session;
    private readonly IPublicContentProjector _projector;
    private readonly PagesOptions _deployment;
    private readonly TenantContext _tenant;
    private readonly IConfiguration _config;

    public Endpoint(
        IQuerySession session,
        IPublicContentProjector projector,
        IOptions<PagesOptions> options,
        TenantContext tenant,
        IConfiguration config)
    {
        _tenant = tenant;
        _config = config;
        _session = session;
        _projector = projector;
        _deployment = options.Value;
    }

    public override void Configure()
    {
        Get("/api/public/pages/resolve");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!ViewerAudience.TryRead(HttpContext, _config, _tenant.Slug, out var member))
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var raw = Query<string>("path", isRequired: false) ?? "/";
        var segments = raw.Length > MaxPathLength
            ? null
            : raw.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var options = await PagesSettings.ResolveAsync(_session, _deployment, ct);
        var definition = await PublishedPages.DefinitionAsync(_session, options.ContentType, ct);
        if (segments is null || segments.Length > options.MaxDepth + 1 || !_projector.IsDeliverable(definition))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var slug = segments.Length == 0 ? options.HomeSlug : segments[^1];
        if (string.IsNullOrWhiteSpace(slug))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var requested = "/" + string.Join('/', segments);
        var candidates = await PublishedPages.WithSlugAsync(_session, _projector, definition!, options, slug, member, ct);

        foreach (var (node, entry) in candidates)
        {
            var nodes = new List<PageNode> { node };
            var parent = node.ParentId;
            for (var depth = 0; parent is { } parentId && depth < options.MaxDepth; depth++)
            {
                if (await PublishedPages.ByIdAsync(_session, _projector, definition!, options, parentId, member, ct) is not { } ancestor)
                {
                    break;
                }

                nodes.Add(ancestor.Node);
                parent = ancestor.Node.ParentId;
            }

            var tree = new PageTree(nodes, options.HomeSlug, options.MaxDepth);
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

            // The walk projects without reading files, which leaves file fields out. The page served
            // is projected again with its files answered as the core delivery routes answer them.
            var served = await _session.LoadAsync<barakoCMS.Models.Content>(entry.Id, ct) is { } doc
                ? await _projector.ProjectAsync(doc, definition, member, ct) ?? entry
                : entry;

            if (member)
            {
                ViewerAudience.SetPrivateCache(HttpContext);
            }
            else
            {
                PublishedPages.SetCache(HttpContext, varyOnViewer: true);
            }

            var bound = await BoundBlocks.MaterializeAsync(_session, _projector, definition!, served, ct);
            await Send.OkAsync(new ResolveResponse(PagesContract.Version, path, served, breadcrumbs, bound), ct);
            return;
        }

        await Send.NotFoundAsync(ct);
    }
}
