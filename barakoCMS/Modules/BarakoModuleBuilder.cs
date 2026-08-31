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
    /// <remarks>
    /// Registering the same module class twice is refused. It used to be skipped and nothing was
    /// said, so a host that deliberately added two configured instances got one of them and no
    /// explanation. A repeat is a configuration mistake, not a preference, and this matches how
    /// <see cref="ModuleOrder"/> already treats two modules sharing a name.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The module's type is already registered.</exception>
    public BarakoModuleBuilder Add(IBarakoModule module)
    {
        ArgumentNullException.ThrowIfNull(module);

        if (IsRegistered(module.GetType()))
        {
            throw new InvalidOperationException(
                $"Module {module.GetType().FullName} is already registered. Register it once. "
                + "Two instances of one module class cannot both be enabled: they share a name, "
                + "so DependsOn could not tell them apart.");
        }

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
    /// <remarks>
    /// A type already registered is skipped rather than refused, which is the one place the
    /// distinction matters. Discovery is a sweep, not a statement of intent: adding a module
    /// explicitly and then scanning the assembly it lives in is a normal combination, and scanning
    /// the same assembly twice finds the same types by definition. <see cref="Add"/> is where a
    /// host says "enable this one", so that is where a repeat is a mistake.
    /// </remarks>
    public BarakoModuleBuilder DiscoverFrom(params Assembly[] assemblies)
    {
        foreach (var asm in assemblies)
        {
            foreach (var type in asm.GetTypes())
            {
                if (!type.IsAbstract && !type.IsInterface &&
                    typeof(IBarakoModule).IsAssignableFrom(type) &&
                    type.GetConstructor(Type.EmptyTypes) != null &&
                    !IsRegistered(type))
                {
                    _modules.Add((IBarakoModule)Activator.CreateInstance(type)!);
                }
            }
        }
        return this;
    }

    private bool IsRegistered(Type type) => _modules.Any(m => m.GetType() == type);
}
