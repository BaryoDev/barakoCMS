using Marten;

namespace BarakoCMS.Tagging;

/// <summary>One vocabulary's terms in memory, for the questions that walk the hierarchy.</summary>
internal sealed class TermTree
{
    private readonly Dictionary<Guid, Term> _byId;
    private readonly ILookup<Guid, Term> _children;

    private TermTree(IReadOnlyList<Term> terms)
    {
        _byId = terms.ToDictionary(t => t.Id);
        _children = terms.Where(t => t.ParentId.HasValue).ToLookup(t => t.ParentId!.Value);
    }

    public int Count => _byId.Count;

    public static async Task<TermTree> LoadAsync(IQuerySession session, Guid vocabularyId, CancellationToken ct) =>
        new(await session.Query<Term>().Where(t => t.VocabularyId == vocabularyId).ToListAsync(ct));

    public bool Contains(Guid id) => _byId.ContainsKey(id);

    /// <summary>Levels from the top down to this term, counting the top level as one.</summary>
    public int DepthOf(Guid id)
    {
        var depth = 0;
        var seen = new HashSet<Guid>();
        Guid? current = id;
        while (current is { } at && _byId.TryGetValue(at, out var term) && seen.Add(at))
        {
            depth++;
            current = term.ParentId;
        }

        return depth;
    }

    /// <summary>Levels in the subtree below and including this term; a term with no children is one.</summary>
    public int HeightOf(Guid id)
    {
        var height = 0;
        var level = new List<Guid> { id };
        var seen = new HashSet<Guid>();
        while (level.Count > 0)
        {
            height++;
            level = level.Where(seen.Add).SelectMany(l => _children[l]).Select(t => t.Id).ToList();
        }

        return height;
    }

    public bool HasChildren(Guid id) => _children[id].Any();

    /// <summary>The term itself and every term below it.</summary>
    public List<Guid> SelfAndDescendants(Guid id)
    {
        var all = new List<Guid>();
        var seen = new HashSet<Guid>();
        var pending = new Stack<Guid>([id]);
        while (pending.Count > 0)
        {
            var next = pending.Pop();
            if (!seen.Add(next)) continue;
            all.Add(next);
            foreach (var child in _children[next]) pending.Push(child.Id);
        }

        return all;
    }
}
