using barakoCMS.Core.Interfaces;
using FastEndpoints;
using Marten;
using Microsoft.Extensions.Options;

namespace BarakoCMS.Pages.Features.Navigation;

internal sealed record NavigationItem(
    Guid Id,
    string? Title,
    string Slug,
    string Path,
    int? Order,
    IReadOnlyList<NavigationItem> Children);

internal sealed record NavigationResponse(int Contract, bool Truncated, IReadOnlyList<NavigationItem> Items);

/// <summary>
/// GET /api/public/pages/navigation: the nested, ordered tree of published pages flagged for
/// navigation, each with its absolute path.
/// </summary>
/// <remarks>
/// A flagged page whose parent is not flagged sits under its nearest flagged ancestor, or at the top
/// when it has none, and keeps its real path. A page with no path (under a draft, a Sensitive page or
/// a missing parent) is left out, since its URL would not resolve.
///
/// At most <see cref="PagesOptions.MaxPages"/> published pages are read, oldest first, and
/// <c>truncated</c> says when there were more. A page past the limit, and every page under it, is
/// missing from the menu while resolve still serves it, so a renderer needs to be told.
/// </remarks>
internal sealed class Endpoint : EndpointWithoutRequest<NavigationResponse>
{
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
        Get("/api/public/pages/navigation");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var definition = await PublishedPages.DefinitionAsync(_session, _options.ContentType, ct);
        if (!_projector.IsDeliverable(definition))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var (pages, truncated) = await PublishedPages.AllAsync(_session, _projector, definition!, _options, ct);
        var tree = new PageTree(pages.Select(p => p.Node), _options.HomeSlug, _options.MaxDepth);

        var inNavigation = new Dictionary<Guid, (PageNode Node, string Path, Guid? NavParent)>();
        foreach (var node in tree.Nodes.Where(n => n.ShowInNavigation))
        {
            if (tree.Ancestry(node.Id) is { } chain)
            {
                inNavigation[node.Id] = (node, tree.PathOf(chain), null);
            }
        }

        foreach (var id in inNavigation.Keys.ToList())
        {
            var chain = tree.Ancestry(id)!;
            Guid? navParent = null;
            for (var i = chain.Count - 2; i >= 0; i--)
            {
                if (inNavigation.ContainsKey(chain[i].Id))
                {
                    navParent = chain[i].Id;
                    break;
                }
            }

            inNavigation[id] = inNavigation[id] with { NavParent = navParent };
        }

        var byParent = inNavigation.Values.ToLookup(v => v.NavParent);

        IReadOnlyList<NavigationItem> Build(Guid? parent) =>
            PageTree.Ordered(byParent[parent].Select(v => v.Node))
                .Select(n => new NavigationItem(n.Id, n.Title, n.Slug!, inNavigation[n.Id].Path, n.Order, Build(n.Id)))
                .ToList();

        PublishedPages.SetCache(HttpContext);
        await Send.OkAsync(new NavigationResponse(PagesContract.Version, truncated, Build(null)), ct);
    }
}
