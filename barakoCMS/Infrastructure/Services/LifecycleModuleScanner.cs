using barakoCMS.Modules;
using Marten;

namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// Keeps what the module that seeded a type put in its lifecycle: its states, its transitions and
/// its initial state. Adding states and transitions stays allowed.
/// </summary>
/// <remarks>
/// The floor is the record the seed context wrote for this tenant and type, and it applies only
/// while that module is still registered in the host. Something the tenant already lacks is not
/// held against the edit.
/// </remarks>
internal sealed class LifecycleModuleScanner(IEnumerable<IBarakoModule> modules) : ILifecycleScanner
{
    public async Task<LifecycleScan> ScanAsync(IQuerySession session, LifecycleChange change, CancellationToken ct)
    {
        if (change.From is null)
        {
            return LifecycleScan.None;
        }

        var seeded = await session.LoadAsync<ModuleSeededType>(ModuleSeededType.KeyOf(change.Type.Name), ct);
        if (seeded is null || !modules.Any(m => string.Equals(m.Name, seeded.Module, StringComparison.OrdinalIgnoreCase)))
        {
            return LifecycleScan.None;
        }

        var blockers = new List<LifecycleBlocker>();
        foreach (var state in seeded.States.Where(s => change.From.States.Contains(s, StringComparer.Ordinal)
                     && !change.To.States.Contains(s, StringComparer.Ordinal)))
        {
            blockers.Add(new LifecycleBlocker(
                LifecycleBlockerKind.Module, state, null, seeded.Module, null,
                $"The state '{state}' was seeded by the module '{seeded.Module}', which this edit removes or renames."));
        }

        foreach (var seededTransition in seeded.Transitions)
        {
            if (LifecycleReferences.Changes(change, seededTransition.Name) is { } changed)
            {
                blockers.Add(new LifecycleBlocker(
                    LifecycleBlockerKind.Module, null, changed.Name, seeded.Module, null,
                    $"The transition '{changed.Name}' was seeded by the module '{seeded.Module}', which this edit removes or changes."));
            }
        }

        if (seeded.InitialState is { } initial
            && string.Equals(change.From.InitialState, initial, StringComparison.Ordinal)
            && !string.Equals(change.To.InitialState, initial, StringComparison.Ordinal))
        {
            blockers.Add(new LifecycleBlocker(
                LifecycleBlockerKind.Module, initial, null, seeded.Module, null,
                $"The initial state '{initial}' was seeded by the module '{seeded.Module}', which this edit changes."));
        }

        return LifecycleScan.Blocking(blockers);
    }
}
