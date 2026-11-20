using barakoCMS.Models;
using Marten;
using Marten.Linq.MatchesSql;

namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// Finds the entries a lifecycle edit would strand: those in a state it removes or renames, and
/// every entry when it moves the initial state or adds a lifecycle.
/// </summary>
/// <remarks>
/// Counted in the database and read into no memory. A state matches ignoring case, because an
/// entry holds the spelling it was moved into and a rule or a transition matches ignoring case. An
/// entry with no stored state is in the initial state the type declares now, so it is counted
/// under the stored initial state. Every branch counts: the query has no branch filter, and the
/// session limits it to its tenant. A change of the initial state is refused whenever the type has
/// an entry, because a projection rebuild of an event sourced type leaves entries that never moved
/// with no state, and no state reads as the initial state.
/// </remarks>
internal sealed class LifecycleEntryScanner : ILifecycleScanner
{
    public async Task<LifecycleScan> ScanAsync(IQuerySession session, LifecycleChange change, CancellationToken ct)
    {
        var blockers = new List<LifecycleBlocker>();

        if (change.From is null)
        {
            var entries = await CountAsync(session, change.Type.Name, ct);
            if (entries > 0)
            {
                blockers.Add(new LifecycleBlocker(
                    LifecycleBlockerKind.InitialState, change.To.InitialState, null, null, entries,
                    $"The type has {Entries(entries)} and no lifecycle. A lifecycle is added only to a type with no entries."));
            }

            return LifecycleScan.Blocking(blockers);
        }

        foreach (var state in change.RemovedStates)
        {
            var entries = await CountInStateAsync(session, change.Type.Name, state, change.From.InitialState, ct);
            if (entries > 0)
            {
                blockers.Add(new LifecycleBlocker(
                    LifecycleBlockerKind.EntryState, state, null, null, entries,
                    $"{Entries(entries)} {(entries == 1 ? "is" : "are")} in '{state}', which this edit removes or renames. "
                    + "Move them to another state first."));
            }
        }

        if (!string.Equals(change.From.InitialState, change.To.InitialState, StringComparison.OrdinalIgnoreCase))
        {
            var entries = await CountAsync(session, change.Type.Name, ct);
            if (entries > 0)
            {
                blockers.Add(new LifecycleBlocker(
                    LifecycleBlockerKind.InitialState, change.From.InitialState, null, null, entries,
                    $"The type has {Entries(entries)}, so its initial state stays '{change.From.InitialState}'."));
            }
        }

        return LifecycleScan.Blocking(blockers);
    }

    /// <summary>How many entries of the type hold the state, with no stored state counted as the initial state.</summary>
    /// <remarks>
    /// The one method that runs the state count. Its two values are bound as parameters, and the
    /// fragment names the document alias <c>d</c> and no value.
    /// </remarks>
    internal static async Task<int> CountInStateAsync(
        IQuerySession session, string contentType, string state, string initialState, CancellationToken ct)
    {
        var lowered = contentType.ToLower();
        object[] args = [initialState, state];
        return await session.Query<Content>()
            .Where(c => c.ContentType.ToLower() == lowered
                && c.MatchesSql("lower(coalesce(nullif(d.data ->> 'LifecycleState', ''), ?)) = lower(?)", args))
            .CountAsync(ct);
    }

    private static async Task<int> CountAsync(IQuerySession session, string contentType, CancellationToken ct)
    {
        var lowered = contentType.ToLower();
        return await session.Query<Content>().CountAsync(c => c.ContentType.ToLower() == lowered, ct);
    }

    private static string Entries(int count) => count == 1 ? "1 entry" : $"{count} entries";
}
