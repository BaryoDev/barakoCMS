using System.Reflection;
using System.Text.RegularExpressions;
using barakoCMS.Modules;

namespace barakoCMS.Infrastructure.Realtime;

public sealed record RealtimeTopic(string Name, string Module, string Capability, IReadOnlyList<Type> EventTypes);

/// <summary>
/// Every topic the running modules declared, built once at startup. A bad declaration throws here,
/// so a host with a broken module does not start.
/// </summary>
public sealed partial class TopicRegistry
{
    [GeneratedRegex("^[a-z0-9]+([.-][a-z0-9]+)*$")]
    private static partial Regex NamePattern();

    private readonly Dictionary<string, RealtimeTopic> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, List<RealtimeTopic>> _byType = new();

    public TopicRegistry(IEnumerable<IBarakoModule> modules)
    {
        foreach (var module in modules)
            module.ConfigureTopics(new Collector(this, module));
    }

    public IReadOnlyCollection<RealtimeTopic> All => _byName.Values;

    public RealtimeTopic? Find(string name) => _byName.GetValueOrDefault(name);

    public IReadOnlyList<RealtimeTopic> TopicsFor(Type eventType) =>
        _byType.TryGetValue(eventType, out var topics) ? topics : [];

    /// <summary>
    /// Refuses a topic that names an event type its module never registered with
    /// <c>AddEventType</c>. Such a topic would pass every other check and then never match an event.
    /// </summary>
    /// <param name="registeredEventTypes">The event types the store knows.</param>
    public void EnsureRegistered(IEnumerable<Type> registeredEventTypes)
    {
        var known = registeredEventTypes.ToHashSet();
        foreach (var topic in _byName.Values)
        foreach (var type in topic.EventTypes)
        {
            if (known.Contains(type)) continue;
            throw new InvalidOperationException(
                $"Module '{topic.Module}' topic '{topic.Name}' names event type {type.FullName}, "
                + "which the module never registered with AddEventType.");
        }
    }

    private void Add(IBarakoModule module, string name, string capability, Type[] eventTypes)
    {
        if (string.IsNullOrEmpty(name) || !NamePattern().IsMatch(name))
            throw new ArgumentException(
                $"Module '{module.Name}' declared topic '{name}'. Use lower case words joined by dots or dashes.", nameof(name));
        if (string.IsNullOrWhiteSpace(capability))
            throw new ArgumentException($"Topic '{name}' in module '{module.Name}' needs a capability.", nameof(capability));
        if (eventTypes is null || eventTypes.Length == 0)
            throw new ArgumentException($"Topic '{name}' in module '{module.Name}' needs at least one event type.", nameof(eventTypes));

        var owned = new HashSet<Assembly> { module.GetType().Assembly };
        foreach (var asm in module.SchemaAssemblies) owned.Add(asm);
        foreach (var type in eventTypes)
        {
            if (owned.Contains(type.Assembly)) continue;
            throw new InvalidOperationException(
                $"Module '{module.Name}' put '{type.FullName}' in topic '{name}', but it ships in "
                + $"'{type.Assembly.GetName().Name}'. A topic may only carry event types from the module's own assemblies.");
        }

        if (_byName.TryGetValue(name, out var existing))
            throw new InvalidOperationException(
                $"Topic '{name}' is declared by module '{existing.Module}' and again by module '{module.Name}'. Topic names are shared by every module.");

        var topic = new RealtimeTopic(name, module.Name, capability.Trim(), eventTypes.Distinct().ToArray());
        _byName[name] = topic;
        foreach (var type in topic.EventTypes)
        {
            if (!_byType.TryGetValue(type, out var list)) _byType[type] = list = [];
            list.Add(topic);
        }
    }

    private sealed class Collector(TopicRegistry registry, IBarakoModule module) : IModuleTopics
    {
        public void Add(string name, string capability, params Type[] eventTypes) =>
            registry.Add(module, name, capability, eventTypes);
    }
}
