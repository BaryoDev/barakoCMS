using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FastEndpoints;
using Marten;
using Microsoft.Extensions.Options;

namespace BarakoCMS.Pages.Features.Tree;

internal sealed record TreeItem(
    Guid Id,
    string? Title,
    string? Slug,
    string? Path,
    ContentStatus Status,
    bool ShowInNavigation,
    int? Order,
    IReadOnlyList<TreeItem> Children);

internal sealed record TreeResponse(int Contract, bool Truncated, IReadOnlyList<TreeItem> Items);

/// <summary>
/// GET /api/pages/tree: every page the caller may read, drafts included, nested and ordered, for the
/// console.
/// </summary>
/// <remarks>
/// Each entry passes the same read check <c>GET /api/contents/{id}</c> applies, and its data is masked
/// the same way before a title or slug is read from it. A page whose parent the caller cannot see, or
/// whose chain loops, is listed at the top level so it can still be found and fixed. <c>path</c> is
/// null for a page whose chain does not reach a root. At most <see cref="PagesOptions.MaxPages"/>
/// pages are read, oldest first, and <c>truncated</c> says when there were more.
/// </remarks>
internal sealed class Endpoint : EndpointWithoutRequest<TreeResponse>
{
    private readonly IQuerySession _session;
    private readonly IPermissionResolver _permissions;
    private readonly ISensitivityService _sensitivity;
    private readonly IPublicContentProjector _projector;
    private readonly PagesOptions _options;

    public Endpoint(
        IQuerySession session,
        IPermissionResolver permissions,
        ISensitivityService sensitivity,
        IPublicContentProjector projector,
        IOptions<PagesOptions> options)
    {
        _session = session;
        _permissions = permissions;
        _sensitivity = sensitivity;
        _projector = projector;
        _options = options.Value;
    }

    public override void Configure()
    {
        Get("/api/pages/tree");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (User.FindFirst("UserId") is not { } claim || !Guid.TryParse(claim.Value, out var userId))
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var user = await _session.LoadAsync<User>(userId, ct);
        if (user is null)
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        var definition = await PublishedPages.DefinitionAsync(_session, _options.ContentType, ct);
        if (definition is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var name = definition.Name;
        var docs = await _session.Query<Content>()
            .Where(c => c.ContentType == name)
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .Take(_options.MaxPages + 1)
            .ToListAsync(ct);

        var truncated = docs.Count > _options.MaxPages;
        var slugField = _projector.SlugField(definition);
        var nodes = new List<PageNode>();
        var statuses = new Dictionary<Guid, ContentStatus>();

        foreach (var doc in docs.Take(_options.MaxPages))
        {
            if (!await _permissions.CanPerformActionAsync(user, name, "read", doc, ct))
            {
                continue;
            }

            var data = new Dictionary<string, object>(doc.Data);
            var hidden = await _sensitivity.ApplyAsync(name, doc.Sensitivity, data, HttpContext, ct);

            nodes.Add(new PageNode(
                doc.Id,
                hidden || slugField is null ? null : PageData.String(data, slugField),
                hidden ? null : PageData.String(data, _options.TitleField),
                PageData.Guid(doc.Data, _options.ParentField),
                PageData.Bool(data, _options.ShowInNavigationField),
                PageData.Int(data, _options.OrderField)));
            statuses[doc.Id] = doc.Status;
        }

        var tree = new PageTree(nodes, _options.HomeSlug, _options.MaxDepth);
        var byParent = tree.Nodes.ToLookup(n =>
            n.ParentId is { } p && tree.Contains(p) && tree.Terminates(n.Id) ? n.ParentId : null);

        IReadOnlyList<TreeItem> Build(Guid? parent) =>
            PageTree.Ordered(byParent[parent])
                .Select(n => new TreeItem(
                    n.Id, n.Title, n.Slug, tree.PathOf(n.Id), statuses[n.Id], n.ShowInNavigation, n.Order, Build(n.Id)))
                .ToList();

        await Send.OkAsync(new TreeResponse(PagesContract.Version, truncated, Build(null)), ct);
    }
}
