namespace BarakoCMS.Pages;

/// <summary>One page as the tree sees it: identity, place in the tree, and what navigation needs.</summary>
internal sealed record PageNode(
    Guid Id,
    string? Slug,
    string? Title,
    Guid? ParentId,
    bool ShowInNavigation,
    int? Order);

/// <summary>
/// Paths, ancestry and sibling order over a set of pages that has already been loaded and filtered.
/// </summary>
/// <remarks>
/// Only the pages given exist. A page whose parent is not in the set has no path, which is what keeps
/// a published page under a draft off the public site: its ancestor was never handed in, so its URL
/// cannot be built and nothing about the draft is disclosed.
///
/// Every walk stops after <c>maxDepth</c> ancestors, so a loop stored by a path that does not run the
/// hook ends the walk rather than the request.
/// </remarks>
internal sealed class PageTree
{
    private readonly Dictionary<Guid, PageNode> _byId;
    private readonly string? _homeSlug;
    private readonly int _maxDepth;

    public PageTree(IEnumerable<PageNode> nodes, string? homeSlug, int maxDepth)
    {
        _byId = new Dictionary<Guid, PageNode>();
        foreach (var node in nodes)
        {
            _byId.TryAdd(node.Id, node);
        }

        _homeSlug = string.IsNullOrWhiteSpace(homeSlug) ? null : homeSlug.Trim();
        _maxDepth = maxDepth;
    }

    public IReadOnlyCollection<PageNode> Nodes => _byId.Values;

    public bool Contains(Guid id) => _byId.ContainsKey(id);

    /// <summary>
    /// The pages from the root down to <paramref name="id"/>, or null when the chain does not reach a
    /// root inside this set within the depth limit, or a page on it has no slug.
    /// </summary>
    public IReadOnlyList<PageNode>? Ancestry(Guid id)
    {
        var chain = new List<PageNode>();
        Guid? current = id;

        while (current is { } at)
        {
            if (!_byId.TryGetValue(at, out var node) || string.IsNullOrWhiteSpace(node.Slug) || chain.Count > _maxDepth)
            {
                return null;
            }

            chain.Add(node);
            current = node.ParentId;
        }

        chain.Reverse();
        return chain;
    }

    /// <summary>
    /// Whether walking up from <paramref name="id"/> ends, at a root or at a parent outside the set,
    /// rather than looping or running past the depth limit.
    /// </summary>
    public bool Terminates(Guid id)
    {
        Guid? current = id;
        for (var steps = 0; steps <= _maxDepth + 1; steps++)
        {
            if (current is not { } at || !_byId.TryGetValue(at, out var node))
            {
                return true;
            }

            current = node.ParentId;
        }

        return false;
    }

    /// <summary>The absolute path of a page, or null when it has none. See <see cref="Ancestry"/>.</summary>
    public string? PathOf(Guid id) => Ancestry(id) is { } chain ? PathOf(chain) : null;

    public string PathOf(IReadOnlyList<PageNode> chain)
    {
        if (chain.Count == 1 && _homeSlug is not null
            && string.Equals(chain[0].Slug, _homeSlug, StringComparison.OrdinalIgnoreCase))
        {
            return "/";
        }

        return "/" + string.Join('/', chain.Select(n => n.Slug!.Trim()));
    }

    /// <summary>Navigation order: the order field ascending with unset last, then title, then slug, then id.</summary>
    public static IOrderedEnumerable<PageNode> Ordered(IEnumerable<PageNode> siblings) =>
        siblings
            .OrderBy(n => n.Order is null ? 1 : 0)
            .ThenBy(n => n.Order)
            .ThenBy(n => n.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(n => n.Slug, StringComparer.OrdinalIgnoreCase)
            .ThenBy(n => n.Id);
}
