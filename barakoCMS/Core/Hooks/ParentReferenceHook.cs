using System.Text.Json;
using barakoCMS.Core.Interfaces;
using barakoCMS.Models;
using Marten;
using Marten.Linq.MatchesSql;

namespace barakoCMS.Core.Hooks;

/// <summary>
/// Declares a reference field on a content type to be that entry's parent, and refuses a write that
/// would make the parent chain point at the entry itself or loop back to it.
/// </summary>
/// <remarks>
/// <para>
/// A reference is not a parent by virtue of pointing at its own type. <c>RelatedPost</c> pointing
/// back at a post that points forward is legitimate, so the schema validator cannot refuse a loop
/// for every self-typed reference. Registering this hook for a type and field is the declaration:
/// <c>services.AddScoped&lt;IContentLifecycleHook&gt;(_ =&gt; new ParentReferenceHook("page", "ParentPage"))</c>.
/// A type without one keeps accepting whatever it accepted before.
/// </para>
/// <para>
/// A create is never checked. Its id is minted after the write is accepted, so nothing stored can
/// point at it yet and it can be neither its own parent nor part of a loop.
/// </para>
/// <para>
/// The walk up the chain runs inside the write's transaction, after a transaction-scoped advisory
/// lock on this tenant, type and field. Without the lock, two saves that each add one edge of a loop
/// (A under B, B under A) both read a tree with no loop in it and both commit. With it, the second
/// save waits until the first commits or rolls back, and then reads the committed parent. The lock
/// is released by the commit or rollback, so it is held only while a write to this tree is in flight.
/// </para>
/// <para>
/// A save that keeps the parent the entry already has is not checked. The edge is already stored,
/// and the write is bound to the version it read, so a concurrent move that changed the parent
/// in between fails that write instead of letting it put the old edge back. Skipping the lock keeps
/// ordinary edits under one parent from queueing behind each other.
/// </para>
/// <para>
/// The walk reads at most <see cref="MaxDepth"/> ancestors. A chain that has not reached a root by
/// then is refused, which also covers a loop already stored above the new parent by some path that
/// does not run this hook. A move carries the entry's descendants with it, so the entries below it
/// are counted too: ancestors plus the height of the moved subtree may not exceed the limit.
/// </para>
/// </remarks>
public sealed class ParentReferenceHook : IContentLifecycleHook
{
    /// <summary>How many ancestors an entry may have when no limit is given.</summary>
    public const int DefaultMaxDepth = 64;

    public ParentReferenceHook(string contentType, string parentField, int maxDepth = DefaultMaxDepth)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentException.ThrowIfNullOrWhiteSpace(parentField);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDepth, 1);

        ContentType = contentType;
        ParentField = parentField;
        MaxDepth = maxDepth;
    }

    public string ContentType { get; }

    /// <summary>The reference field that names the entry's parent.</summary>
    public string ParentField { get; }

    /// <summary>The most ancestors an entry may have, and so the most entries the walk reads.</summary>
    public int MaxDepth { get; }

    public async Task<IReadOnlyList<string>> OnBeforeSaveAsync(ContentLifecycleContext context, CancellationToken ct)
    {
        if (context.EntryId is not { } self)
        {
            return [];
        }

        if (ReadParent(context.Data) is not { } parent)
        {
            return [];
        }

        if (parent == self)
        {
            return [$"Field '{await DisplayNameAsync(context.Session, ct)}' cannot point at the entry itself."];
        }

        if (context.Existing is { } existing && ReadParent(existing) == parent)
        {
            return [];
        }

        await context.Session.BeginTransactionAsync(ct);
        await context.Session.QueryAsync<int>(
            "select 1 from pg_advisory_xact_lock(hashtextextended(?, 0))",
            ct,
            $"barakocms:parent-reference:{context.Session.TenantId}:{ContentType.ToLowerInvariant()}:{ParentField.ToLowerInvariant()}");

        var (error, ancestors) = await WalkAsync(context.Session, self, parent, ct);

        // An entry that is not stored yet (PageTreeHook walks a create under a minted id) has nothing
        // below it, so there is no subtree to measure.
        if (error is null && context.Existing is not null
            && await SubtreeHeightAsync(context.Session, self, MaxDepth - ancestors + 1, ct) > MaxDepth - ancestors)
        {
            error = $"would put an entry below this one more than {MaxDepth} levels deep.";
        }

        return error is null ? [] : [$"Field '{await DisplayNameAsync(context.Session, ct)}' {error}"];
    }

    /// <summary>
    /// The ancestor count is how many entries the walk loaded before reaching a root, a parent of
    /// another type, or a missing parent.
    /// </summary>
    private async Task<(string? Error, int Ancestors)> WalkAsync(IDocumentSession session, Guid self, Guid parent, CancellationToken ct)
    {
        var visited = new HashSet<Guid>();
        var current = parent;

        for (var depth = 1; depth <= MaxDepth; depth++)
        {
            var ancestor = await session.LoadAsync<Content>(current, ct);
            if (ancestor is null || !string.Equals(ancestor.ContentType, ContentType, StringComparison.OrdinalIgnoreCase))
            {
                return (null, depth - 1);
            }

            visited.Add(current);

            if (ReadParent(ancestor.Data) is not { } next)
            {
                return (null, depth);
            }

            if (next == self)
            {
                return ($"would make a cycle: this entry is already an ancestor of {parent}.", depth);
            }

            if (visited.Contains(next))
            {
                return ($"points at {parent}, whose parent chain loops and never reaches a root.", depth);
            }

            if (depth == MaxDepth)
            {
                break;
            }

            current = next;
        }

        return ($"would put this entry more than {MaxDepth} levels deep.", MaxDepth);
    }

    /// <summary>
    /// How many levels of descendants <paramref name="self"/> has, reading one level per query and
    /// stopping once <paramref name="limit"/> levels are found, so a deep tree costs at most that
    /// many queries. An entry already seen is not followed again, so a loop stored below this entry
    /// by another write path still ends.
    /// </summary>
    private async Task<int> SubtreeHeightAsync(IDocumentSession session, Guid self, int limit, CancellationToken ct)
    {
        var seen = new HashSet<Guid> { self };
        var frontier = new[] { self.ToString() };
        var lowered = ContentType.ToLower();

        for (var height = 0; height < limit; height++)
        {
            var children = await session.Query<Content>()
                .Where(c => c.ContentType.ToLower() == lowered && c.MatchesSql(ParentIsAnyOfSql, ParentField, frontier))
                .Select(c => c.Id)
                .ToListAsync(ct);

            var next = children.Where(seen.Add).Select(id => id.ToString()).ToArray();
            if (next.Length == 0)
            {
                return height;
            }

            frontier = next;
        }

        return limit;
    }

    /// <summary>
    /// The parent field, matched by name without regard to case like <see cref="ReadParent"/>, holds
    /// one of the given ids. Both are bound parameters.
    /// </summary>
    private const string ParentIsAnyOfSql =
        "lower((SELECT e.value FROM jsonb_each(d.data -> 'Data') e WHERE lower(e.key) = lower(?) LIMIT 1) #>> '{}') = ANY(?)";

    private Guid? ReadParent(IReadOnlyDictionary<string, object> data)
    {
        var value = data.FirstOrDefault(kv => string.Equals(kv.Key, ParentField, StringComparison.OrdinalIgnoreCase)).Value;
        var raw = value switch
        {
            null => null,
            JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
            JsonElement => null,
            _ => value.ToString(),
        };

        return Guid.TryParse(raw, out var id) ? id : null;
    }

    private async Task<string> DisplayNameAsync(IDocumentSession session, CancellationToken ct)
    {
        var lowered = ContentType.ToLower();
        var definition = await session.Query<ContentTypeDefinition>()
            .FirstOrDefaultAsync(d => d.Name.ToLower() == lowered, ct);

        return definition?.Fields
            .FirstOrDefault(f => string.Equals(f.Name, ParentField, StringComparison.OrdinalIgnoreCase))
            ?.DisplayName is { Length: > 0 } display
            ? display
            : ParentField;
    }
}
