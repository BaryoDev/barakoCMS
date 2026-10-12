namespace barakoCMS.Modules;

/// <summary>
/// Where a module names the workflow triggers it fires: something the module knows happened, such
/// as a form submitted or a payment received, that a workflow can listen for.
/// </summary>
/// <remarks>
/// A workflow saved with a trigger nothing registered is refused, so naming the trigger here is what
/// makes it one an operator can choose. Firing it is <see cref="barakoCMS.Core.Interfaces.IWorkflowTriggers"/>.
/// </remarks>
public interface IModuleWorkflowTriggers
{
    /// <param name="name">
    /// Lower case words joined by dots or dashes, with at least one dot, for example
    /// <c>forms.submitted</c>. Unique across modules. The dot keeps a module's names apart from
    /// core's own triggers, which are single capitalised words.
    /// </param>
    /// <param name="description">What happened, in a sentence an operator choosing the trigger can read.</param>
    void Add(string name, string description);
}
