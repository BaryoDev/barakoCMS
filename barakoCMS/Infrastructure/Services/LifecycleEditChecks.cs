using barakoCMS.Models;
using Marten;

namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// The fixed set of things that can block, or warn about, a lifecycle edit. The strings are part of
/// the response of <c>PUT /api/content-types/{name}/lifecycle</c>, so they do not change.
/// </summary>
internal static class LifecycleBlockerKind
{
    /// <summary>Entries hold a state the edit removes or renames.</summary>
    public const string EntryState = "entryState";

    /// <summary>The initial state changes while the type has entries, or a lifecycle is added to a type that has entries.</summary>
    public const string InitialState = "initialState";

    /// <summary>A transition names a field the type does not have. The route answers this as a 400 before any check runs.</summary>
    public const string TransitionField = "transitionField";

    /// <summary>A role permission names a transition the edit changes or removes.</summary>
    public const string Role = "role";

    /// <summary>A workflow trigger names a transition the edit changes or removes.</summary>
    public const string WorkflowTrigger = "workflowTrigger";

    /// <summary>A workflow action moves an entry through a transition the edit changes or removes.</summary>
    public const string WorkflowAction = "workflowAction";

    /// <summary>A pending or running workflow run names a transition the edit changes or removes.</summary>
    public const string WorkflowRun = "workflowRun";

    /// <summary>A rule names a state the edit removes or renames.</summary>
    public const string Rule = "rule";

    /// <summary>A uniqueness rule applies in a state the edit removes or renames.</summary>
    public const string Uniqueness = "uniqueness";

    /// <summary>A schedule trigger fires on entries in a state the edit removes or renames.</summary>
    public const string Schedule = "schedule";

    /// <summary>A warning, never a blocker: a template link names a transition the edit removes.</summary>
    public const string TemplateLink = "templateLink";

    /// <summary>The edit takes away something the module that seeded the type put there.</summary>
    public const string Module = "module";
}

/// <summary>One thing the edit would break, or for a warning, leave dangling.</summary>
/// <param name="Kind">One of <see cref="LifecycleBlockerKind"/>.</param>
/// <param name="State">The state it is about, or null when it is about a transition.</param>
/// <param name="Transition">The transition it is about, or null when it is about a state.</param>
/// <param name="Name">What blocks: a role, a workflow, a rule. Null for entries.</param>
/// <param name="Count">How many, where it has a count: entries, runs. Null otherwise.</param>
/// <param name="Message">A sentence for a person.</param>
internal sealed record LifecycleBlocker(string Kind, string? State, string? Transition, string? Name, int? Count, string Message);

/// <summary>What one scanner found.</summary>
internal sealed record LifecycleScan(IReadOnlyList<LifecycleBlocker> Blockers, IReadOnlyList<LifecycleBlocker> Warnings)
{
    public static readonly LifecycleScan None = new([], []);

    public static LifecycleScan Blocking(IReadOnlyList<LifecycleBlocker> blockers) => new(blockers, []);
}

/// <summary>What every scanner found, in the order the scanners are registered.</summary>
internal sealed record LifecycleCheck(IReadOnlyList<LifecycleBlocker> Blockers, IReadOnlyList<LifecycleBlocker> Warnings)
{
    public bool Allowed => Blockers.Count == 0;
}

/// <summary>A lifecycle edit as the scanners read it: the stored lifecycle and the one proposed.</summary>
internal sealed class LifecycleChange(ContentTypeDefinition type, LifecycleDefinition? from, LifecycleDefinition to)
{
    public ContentTypeDefinition Type { get; } = type;

    /// <summary>The stored lifecycle, or null when the type has none.</summary>
    public LifecycleDefinition? From { get; } = from;

    public LifecycleDefinition To { get; } = to;

    /// <summary>
    /// The stored states the proposed lifecycle does not spell the same way. A case-only change is
    /// one of them: an entry holds the spelling it was moved into.
    /// </summary>
    public IReadOnlyList<string> RemovedStates =>
        (From?.States ?? []).Where(s => !To.States.Contains(s, StringComparer.Ordinal)).ToList();

    /// <summary>
    /// The stored transitions the proposed lifecycle removes, or keeps under the name with another
    /// from or to. A role permission, a trigger and an action link name the transition and mean its
    /// moves, so either change leaves them pointing at something else. Names compare without case,
    /// as the transition route does.
    /// </summary>
    public IReadOnlyList<StateTransition> ChangedTransitions => ChangedBy(StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <see cref="ChangedTransitions"/> where the name compares by spelling too, so a transition
    /// renamed only in case is one of them. For a reader that matches the name exactly: a workflow
    /// trigger and a module's record.
    /// </summary>
    public IReadOnlyList<StateTransition> ChangedTransitionsBySpelling => ChangedBy(StringComparison.Ordinal);

    private List<StateTransition> ChangedBy(StringComparison name) =>
        (From?.Transitions ?? []).Where(old => !To.Transitions.Any(kept =>
            string.Equals(kept.Name, old.Name, name)
            && string.Equals(kept.From, old.From, StringComparison.OrdinalIgnoreCase)
            && string.Equals(kept.To, old.To, StringComparison.OrdinalIgnoreCase))).ToList();
}

/// <summary>
/// Looks for one kind of thing a lifecycle edit would break.
/// </summary>
/// <remarks>
/// The route and its dry run run every registered scanner and list what they find together, so a
/// scanner is added by registering it, in <c>ServiceCollectionExtensions</c>, and nothing else
/// changes. A scanner reads, never writes, and runs under the type's exclusive lock.
/// </remarks>
internal interface ILifecycleScanner
{
    Task<LifecycleScan> ScanAsync(IQuerySession session, LifecycleChange change, CancellationToken ct);
}

/// <summary>Builds the blocker list for a lifecycle edit. The route and its dry run both call it.</summary>
internal sealed class LifecycleEditChecks(IEnumerable<ILifecycleScanner> scanners)
{
    public async Task<LifecycleCheck> CheckAsync(
        IQuerySession session, ContentTypeDefinition type, LifecycleDefinition proposed, CancellationToken ct)
    {
        var change = new LifecycleChange(type, type.Lifecycle, proposed);
        var blockers = new List<LifecycleBlocker>();
        var warnings = new List<LifecycleBlocker>();

        foreach (var scanner in scanners)
        {
            var scan = await scanner.ScanAsync(session, change, ct);
            blockers.AddRange(scan.Blockers);
            warnings.AddRange(scan.Warnings);
        }

        return new LifecycleCheck(blockers, warnings);
    }
}
