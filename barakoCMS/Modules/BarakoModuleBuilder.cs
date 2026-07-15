using System.Reflection;

namespace barakoCMS.Modules;

/// <summary>
/// Collects the modules a host wants enabled. Passed to <c>AddBarakoCMS</c> via a configuration
/// callback. Supports explicit registration and — optionally — reflection-based discovery.
/// </summary>
public sealed class BarakoModuleBuilder
{
    private readonly List<IBarakoModule> _modules = new();

    public IReadOnlyList<IBarakoModule> Modules => _modules;

    /// <summary>Register a module instance.</summary>
    public BarakoModuleBuilder Add(IBarakoModule module)
    {
        if (_modules.All(m => m.GetType() != module.GetType()))
            _modules.Add(module);
        return this;
    }

    /// <summary>Register a module by type (must have a parameterless constructor).</summary>
    public BarakoModuleBuilder Add<TModule>() where TModule : IBarakoModule, new() => Add(new TModule());

    /// <summary>
    /// Optional convenience: scan the given assemblies for concrete <see cref="IBarakoModule"/>
    /// types with a parameterless constructor and register them. Use when a host prefers
    /// drop-in discovery over explicit wiring.
    /// </summary>
    public BarakoModuleBuilder DiscoverFrom(params Assembly[] assemblies)
    {
        foreach (var asm in assemblies)
        {
            foreach (var type in asm.GetTypes())
            {
                if (!type.IsAbstract && !type.IsInterface &&
                    typeof(IBarakoModule).IsAssignableFrom(type) &&
                    type.GetConstructor(Type.EmptyTypes) != null)
                {
                    Add((IBarakoModule)Activator.CreateInstance(type)!);
                }
            }
        }
        return this;
    }
}
