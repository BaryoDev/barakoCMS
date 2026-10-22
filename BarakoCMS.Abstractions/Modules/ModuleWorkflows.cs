using Marten;
using barakoCMS.Models;

namespace barakoCMS.Modules;

/// <summary>Seeds the workflows a module ships, marked as the module's.</summary>
/// <remarks>
/// A module calls this from its seed instead of storing the definitions itself. Config apply leaves
/// a workflow marked this way alone (see <see cref="WorkflowDefinition.OwnerModule"/>).
/// </remarks>
public static class ModuleWorkflows
{
    /// <summary>
    /// Stores each workflow that is not stored yet, owned by <paramref name="module"/>. One already
    /// stored under the same id keeps its content and is only marked owned, which is how a database
    /// seeded before owners existed picks up the mark.
    /// </summary>
    public static async Task SeedAsync(
        IDocumentSession session, string module, IEnumerable<WorkflowDefinition> workflows, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(module);
        ArgumentNullException.ThrowIfNull(workflows);

        foreach (var workflow in workflows)
        {
            if (await session.LoadAsync<WorkflowDefinition>(workflow.Id, ct) is not { } stored)
            {
                workflow.OwnerModule = module;
                session.Store(workflow);
            }
            else if (stored.OwnerModule is null)
            {
                stored.OwnerModule = module;
                session.Store(stored);
            }
        }
    }
}
