using System.Text.RegularExpressions;
using barakoCMS.Models;
using barakoCMS.Modules;

namespace barakoCMS.Features.Workflows;

/// <summary>A trigger a module registered, and which module did.</summary>
internal sealed record ModuleWorkflowTrigger(string Name, string Module, string Description);

/// <summary>
/// Every trigger a workflow may be saved on: core's content events, a transition the content type
/// declares, and what the running modules registered. Built once at startup.
/// </summary>
/// <remarks>
/// A bad declaration throws while this is built, so a host with a broken module does not start, the
/// same as <see cref="barakoCMS.Infrastructure.Realtime.TopicRegistry"/>.
/// </remarks>
internal sealed partial class WorkflowTriggerRegistry
{
    [GeneratedRegex("^[a-z0-9]+([.-][a-z0-9]+)*$")]
    private static partial Regex NamePattern();

    /// <summary>The longest name a module may register.</summary>
    internal const int MaxNameLength = 100;

    private readonly Dictionary<string, ModuleWorkflowTrigger> _byName = new(StringComparer.Ordinal);

    /// <summary>Core's own triggers and nothing else, for a validator built without a container.</summary>
    internal static readonly WorkflowTriggerRegistry BuiltIn = new([]);

    public WorkflowTriggerRegistry(IEnumerable<IBarakoModule> modules)
    {
        foreach (var module in modules)
        {
            module.ConfigureWorkflowTriggers(new Collector(this, module));
        }
    }

    /// <summary>What modules registered, in name order.</summary>
    public IReadOnlyList<ModuleWorkflowTrigger> ModuleTriggers =>
        _byName.Values.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();

    /// <summary>
    /// Whether a workflow may be saved on this trigger. A transition is accepted here when it is well
    /// formed; whether the type declares it is checked against the type when the workflow is saved.
    /// </summary>
    public bool IsRegistered(string? name) =>
        name is not null && (WorkflowEvents.IsValid(name) || _byName.ContainsKey(name));

    /// <summary>Whether a module registered this name.</summary>
    public bool IsModuleTrigger(string? name) => name is not null && _byName.ContainsKey(name);

    /// <summary>Every name a workflow may name, transitions aside, for an error that lists them.</summary>
    public IEnumerable<string> Names => WorkflowEvents.All.Concat(ModuleTriggers.Select(t => t.Name));

    private void Add(IBarakoModule module, string name, string description)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength || !NamePattern().IsMatch(name) || !name.Contains('.'))
        {
            throw new ArgumentException(
                $"Module '{module.Name}' registered workflow trigger '{name}'. Use lower case words joined by dots or dashes, "
                + $"with at least one dot and at most {MaxNameLength} characters, such as 'forms.submitted'.", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            throw new ArgumentException($"Workflow trigger '{name}' in module '{module.Name}' needs a description.", nameof(description));
        }

        if (_byName.TryGetValue(name, out var existing))
        {
            throw new InvalidOperationException(
                $"Workflow trigger '{name}' is registered by module '{existing.Module}' and again by module '{module.Name}'. "
                + "Trigger names are shared by every module.");
        }

        _byName[name] = new ModuleWorkflowTrigger(name, module.Name, description.Trim());
    }

    private sealed class Collector(WorkflowTriggerRegistry registry, IBarakoModule module) : IModuleWorkflowTriggers
    {
        public void Add(string name, string description) => registry.Add(module, name, description);
    }
}

/// <summary>Fires a module's trigger by queueing runs, in the scope's tenant.</summary>
internal sealed class WorkflowTriggerSource(WorkflowTriggerRegistry registry, IWorkflowRunQueue queue) : barakoCMS.Core.Interfaces.IWorkflowTriggers
{
    public Task<int> FireAsync(string trigger, barakoCMS.Models.Content content, CancellationToken ct) =>
        FireAsync(trigger, content, triggerKey: null, ct);

    public Task<int> FireAsync(string trigger, barakoCMS.Models.Content content, string? triggerKey, CancellationToken ct)
    {
        if (!registry.IsModuleTrigger(trigger))
        {
            throw new ArgumentException(
                $"'{trigger}' is not a workflow trigger a module registered. Register it in ConfigureWorkflowTriggers; "
                + "core's own triggers and transitions are fired by core and cannot be fired here.", nameof(trigger));
        }

        ArgumentNullException.ThrowIfNull(content);

        if (content.Id == Guid.Empty || string.IsNullOrWhiteSpace(content.ContentType))
        {
            throw new ArgumentException("The entry needs an id and a content type for a workflow to run on it.", nameof(content));
        }

        return queue.FireAsync(content, trigger, triggerKey, ct);
    }
}
