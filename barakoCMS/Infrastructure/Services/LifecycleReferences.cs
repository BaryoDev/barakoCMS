using barakoCMS.Features.Workflows;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Infrastructure.Services;

/// <summary>How the reference scanners read what a stored lifecycle and a proposed one have in common.</summary>
internal static class LifecycleReferences
{
    /// <summary>
    /// Whether the edit removes or renames the state a reference names. A reference to a state the
    /// stored lifecycle never had is not this edit's doing, so it never counts.
    /// </summary>
    /// <param name="change">The edit.</param>
    /// <param name="state">The state a reference names.</param>
    /// <param name="exact">True where the reader compares the spelling, so a case-only rename strands it too.</param>
    public static bool Strands(LifecycleChange change, string? state, bool exact = false)
    {
        if (change.From is null || string.IsNullOrWhiteSpace(state))
        {
            return false;
        }

        var comparer = exact ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        return change.From.States.Contains(state, comparer) && !change.To.States.Contains(state, comparer);
    }

    /// <summary>The stored transition the edit removes or changes under this name, or null.</summary>
    public static StateTransition? Changes(LifecycleChange change, string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : change.ChangedTransitions.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>A comma list split into its trimmed, non-empty parts.</summary>
    public static IEnumerable<string> Listed(string? list) =>
        (list ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The transitions a workflow trigger names on this type, by name.</summary>
    public static IEnumerable<string> TriggeredBy(WorkflowDefinition workflow, ContentTypeDefinition type) =>
        WorkflowTriggers.ContentTypes(workflow).Any(t => barakoCMS.Core.ContentTypeName.Same(t, type.Name))
            ? WorkflowTriggers.Events(workflow).Select(WorkflowEvents.TransitionName).OfType<string>()
            : [];
}

/// <summary>The workflows of the tenant, read once for all the scanners of one request.</summary>
internal sealed class LifecycleWorkflows
{
    private IReadOnlyList<WorkflowDefinition>? _all;

    public async Task<IReadOnlyList<WorkflowDefinition>> AllAsync(IQuerySession session, CancellationToken ct) =>
        _all ??= await session.Query<WorkflowDefinition>().ToListAsync(ct);
}
