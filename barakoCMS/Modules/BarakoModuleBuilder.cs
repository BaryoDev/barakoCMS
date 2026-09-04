using System.Reflection;
using Microsoft.Extensions.DependencyModel;

namespace barakoCMS.Modules;

/// <summary>
/// Collects the modules a host wants enabled. Passed to <c>AddBarakoCMS</c> via a configuration
/// callback. Supports explicit registration and reflection-based discovery.
/// </summary>
public sealed class BarakoModuleBuilder
{
    private readonly List<IBarakoModule> _modules = new();

    public IReadOnlyList<IBarakoModule> Modules => _modules;

    /// <summary>
    /// Whether <c>AddBarakoCMS</c> calls <see cref="DiscoverFrom()"/> after the host's callback.
    /// On by default, so referencing a module package is the whole install. Set it to false for a
    /// host that wants only the modules it added by hand. <c>BarakoCMS:Modules:Discover</c> in
    /// configuration sets the starting value; what the callback sets wins.
    /// </summary>
    public bool Discover { get; set; } = true;

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
    /// Scan the given assemblies for public, top-level, concrete <see cref="IBarakoModule"/> types
    /// with a parameterless constructor and register them, ordered by type name.
    /// </summary>
    /// <remarks>
    /// A type already registered is skipped rather than refused, which is the one place the
    /// distinction matters. Discovery is a sweep, not a statement of intent: adding a module
    /// explicitly and then scanning the assembly it lives in is a normal combination, and scanning
    /// the same assembly twice finds the same types by definition. <see cref="Add"/> is where a
    /// host says "enable this one", so that is where a repeat is a mistake.
    ///
    /// Only public top-level types count. A module shipped for other people to reference is public
    /// by construction, and a private nested implementation is a test double or an internal helper,
    /// which discovery has no business constructing.
    /// </remarks>
    public BarakoModuleBuilder DiscoverFrom(params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);

        var candidates = assemblies
            .SelectMany(LoadableTypes)
            .Where(IsDiscoverable)
            .Distinct()
            .OrderBy(t => t.FullName, StringComparer.Ordinal);

        foreach (var type in candidates)
        {
            if (!IsRegistered(type))
                _modules.Add((IBarakoModule)Activator.CreateInstance(type)!);
        }

        return this;
    }

    /// <summary>
    /// Find modules in the application's dependency context: every library that depends on
    /// BarakoCMS is loaded and scanned as <see cref="DiscoverFrom(Assembly[])"/> would.
    /// </summary>
    /// <remarks>
    /// <c>DependencyContext</c> rather than <c>AppDomain.CurrentDomain.GetAssemblies()</c>, because
    /// assemblies load lazily: a referenced module nothing has touched yet is simply absent from
    /// the loaded set, and this is meant to run before anything has touched a module.
    ///
    /// Only libraries that reach BarakoCMS through their dependencies are loaded, so an unrelated
    /// package is never loaded on the chance it holds a module. Reach, not a direct reference:
    /// <c>BarakoCMS.Files.S3</c> references <c>BarakoCMS.Files</c> and nothing else, and a module
    /// built on another module is a shape worth keeping. A library that reaches core and then
    /// fails to load is reported by name rather than skipped: skipping it would look like "my
    /// module does nothing", which is the failure this whole mechanism exists to avoid.
    ///
    /// No dependency context (a host published without a deps.json) finds nothing. Such a host adds
    /// its modules by hand.
    /// </remarks>
    public BarakoModuleBuilder DiscoverFrom()
    {
        var context = DependencyContext.Default;
        if (context is null)
            return this;

        var reachesCore = ReachesCore(context);
        var assemblies = new List<Assembly>();
        foreach (var library in context.RuntimeLibraries)
        {
            if (!reachesCore(library.Name))
                continue;

            foreach (var name in library.GetDefaultAssemblyNames(context))
            {
                try
                {
                    assemblies.Add(Assembly.Load(name));
                }
                catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
                {
                    throw new InvalidOperationException(
                        $"Module discovery could not load assembly '{name}' from '{library.Name}' "
                        + $"{library.Version}, which depends on BarakoCMS and so may hold a module. "
                        + "Fix the reference, or set BarakoCMS:Modules:Discover to false and add "
                        + "modules explicitly.", ex);
                }
            }
        }

        return DiscoverFrom(assemblies.ToArray());
    }

    /// <summary>
    /// The core library as the dependency context names it: the package id, plus the assembly name
    /// in case the two ever differ. A project reference and a package reference both list the id.
    /// </summary>
    private static readonly HashSet<string> CoreLibraryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "BarakoCMS",
        typeof(IBarakoModule).Assembly.GetName().Name!,
    };

    /// <summary>
    /// Whether a library's dependency closure contains core. Memoised, because the same packages
    /// sit under every module; core itself answers false, since it does not depend on itself.
    /// </summary>
    internal static Func<string, bool> ReachesCore(DependencyContext context)
    {
        var dependencies = context.RuntimeLibraries.ToDictionary(
            l => l.Name,
            l => l.Dependencies.Select(d => d.Name).ToArray(),
            StringComparer.OrdinalIgnoreCase);

        var memo = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        bool Reaches(string name)
        {
            if (memo.TryGetValue(name, out var known))
                return known;

            // Provisionally false, which is also what breaks a cycle should the graph hold one.
            memo[name] = false;

            var result = dependencies.TryGetValue(name, out var deps)
                && deps.Any(d => CoreLibraryNames.Contains(d) || Reaches(d));

            memo[name] = result;
            return result;
        }

        return Reaches;
    }

    private static bool IsDiscoverable(Type type) =>
        type.IsPublic
        && !type.IsAbstract
        && !type.IsInterface
        && typeof(IBarakoModule).IsAssignableFrom(type)
        && type.GetConstructor(Type.EmptyTypes) is not null;

    // GetTypes throws away every type that loaded when one does not. The exception keeps them.
    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
    }

    private bool IsRegistered(Type type) => _modules.Any(m => m.GetType() == type);
}
