using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.Extensions.DependencyModel;

namespace barakoCMS.Modules;

/// <summary>
/// Collects the modules a host wants enabled. Passed to <c>AddBarakoCMS</c> via a configuration
/// callback. Supports explicit registration and reflection-based discovery.
/// </summary>
public sealed class BarakoModuleBuilder
{
    private readonly List<IBarakoModule> _modules = new();
    private readonly List<UnloadableModuleAssembly> _skipped = new();

    public IReadOnlyList<IBarakoModule> Modules => _modules;

    /// <summary>Module assemblies discovery passed over because a type in them cannot load.</summary>
    internal IReadOnlyList<UnloadableModuleAssembly> Skipped => _skipped;

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
    /// <c>ModuleOrder</c> already treats two modules sharing a name.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The module's type is already registered, or its assembly has a type that cannot load.
    /// </exception>
    public BarakoModuleBuilder Add(IBarakoModule module)
    {
        ArgumentNullException.ThrowIfNull(module);

        // The endpoint scan leaves such an assembly out, so the module would configure its services
        // and serve none of its endpoints. Refused here, where the host named it.
        var assembly = module.GetType().Assembly;
        if (!TryLoadTypes(assembly, out _, out var missing))
        {
            var unloadable = new UnloadableModuleAssembly(assembly, missing);
            throw new InvalidOperationException(
                $"Module {module.GetType().FullName} cannot be registered: {unloadable} cannot load. "
                + "Update the package to a version built for this barakoCMS.");
        }

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
    /// Every type in <paramref name="assembly"/>, or false with the types that did load and the
    /// names it could not resolve.
    /// </summary>
    /// <remarks>
    /// The usual cause is a module built against a barakoCMS whose types this one no longer has
    /// (#1010). Discovery and the endpoint scan in core both ask here, so they skip the same
    /// assemblies: FastEndpoints calls <c>GetTypes</c> on every assembly it scans, and one that
    /// throws there takes the whole host down.
    /// </remarks>
    internal static bool TryLoadTypes(Assembly assembly, out Type[] types, out IReadOnlyList<string> missing)
    {
        try
        {
            types = assembly.GetTypes();
            missing = [];
            return true;
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.OfType<Type>().ToArray();
            var names = ex.LoaderExceptions
                .Select(e => e switch
                {
                    TypeLoadException t when !string.IsNullOrEmpty(t.TypeName) => t.TypeName,
                    FileNotFoundException f when f.FileName is not null => f.FileName,
                    FileLoadException f when f.FileName is not null => f.FileName,
                    _ => e?.Message,
                })
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList();
            missing = names.Count > 0 ? names : [ex.Message];
            return false;
        }
    }

    /// <summary>
    /// Whether <paramref name="assembly"/> defines a module, judged from <paramref name="loaded"/>
    /// and, for the types that did not load, from its metadata.
    /// </summary>
    /// <remarks>
    /// A module built against an older barakoCMS is exactly the case where the module type itself
    /// fails to load, so the loaded types alone cannot answer. The metadata still says which types
    /// implement <c>barakoCMS.Modules.IBarakoModule</c> by name. An assembly that defines no module
    /// is a library the host depends on, and a type in it that cannot load is the host's build to
    /// fix, so it is not skipped.
    /// </remarks>
    internal static bool DefinesModule(Assembly assembly, IEnumerable<Type> loaded)
    {
        if (loaded.Any(t => typeof(IBarakoModule).IsAssignableFrom(t)))
            return true;

        if (assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location) || !File.Exists(assembly.Location))
            return false;

        using var stream = File.OpenRead(assembly.Location);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata)
            return false;

        var md = pe.GetMetadataReader();
        foreach (var typeHandle in md.TypeDefinitions)
        {
            foreach (var implHandle in md.GetTypeDefinition(typeHandle).GetInterfaceImplementations())
            {
                var iface = md.GetInterfaceImplementation(implHandle).Interface;
                if (iface.Kind != HandleKind.TypeReference)
                    continue;

                var reference = md.GetTypeReference((TypeReferenceHandle)iface);
                if (md.StringComparer.Equals(reference.Name, nameof(IBarakoModule))
                    && md.StringComparer.Equals(reference.Namespace, "barakoCMS.Modules"))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// The core library as the dependency context names it. A project reference and a package
    /// reference both list the id, and the comparer is case-insensitive, so the assembly name
    /// (<c>barakoCMS</c>) matches the same entry.
    /// </summary>
    /// <remarks>
    /// Named rather than read off <c>typeof(IBarakoModule).Assembly</c>, which is the contract
    /// assembly and not the core. Reading it there would put BarakoCMS.Abstractions in this set,
    /// and since core depends on the contract, core would then reach "core" and be scanned for
    /// modules it does not hold.
    /// </remarks>
    private static readonly HashSet<string> CoreLibraryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "BarakoCMS",
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

    // A module assembly with a type that cannot load is skipped whole and recorded, not mined for
    // the types that did. The endpoint scan leaves it out too (see TryLoadTypes), so a module
    // registered from it would run with none of its endpoints mapped. An assembly that defines no
    // module gives up the types that loaded, which hold no module either.
    private IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        if (TryLoadTypes(assembly, out var types, out var missing))
            return types;

        if (!DefinesModule(assembly, types))
            return types;

        if (!_skipped.Any(s => s.Assembly == assembly))
            _skipped.Add(new UnloadableModuleAssembly(assembly, missing));
        return [];
    }

    private bool IsRegistered(Type type) => _modules.Any(m => m.GetType() == type);
}
