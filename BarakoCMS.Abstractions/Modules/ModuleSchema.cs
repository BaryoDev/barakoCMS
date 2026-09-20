using System.Reflection;
using Marten;

namespace barakoCMS.Modules;

/// <summary>
/// Enforces <see cref="IModuleSchema"/> by checking which assembly a document type came from
/// before letting it reach <see cref="StoreOptions"/>.
/// </summary>
internal sealed class ModuleSchema : IModuleSchema
{
    private readonly StoreOptions _options;
    private readonly IBarakoModule _module;
    private readonly HashSet<Assembly> _owned;

    public ModuleSchema(StoreOptions options, IBarakoModule module)
    {
        _options = options;
        _module = module;

        // SchemaAssemblies, deliberately NOT EndpointAssemblies.
        //
        // This used to union both. A module could then list barakoCMS in its endpoint assemblies and
        // legally configure core's documents, which defeats the whole restriction: the guard asked
        // the module what it owned and believed the answer, using a property that exists for an
        // unrelated purpose and that a module has every reason to widen.
        //
        // Still a declaration the module makes, so still not a defence against a hostile module.
        // What it stops is the accident: widening endpoint scanning no longer silently widens what
        // a module may re-map.
        _owned = new HashSet<Assembly> { module.GetType().Assembly };
        foreach (var asm in module.SchemaAssemblies)
            _owned.Add(asm);
    }

    public MartenRegistry.DocumentMappingExpression<T> For<T>()
    {
        var owner = typeof(T).Assembly;
        if (!_owned.Contains(owner))
        {
            // Named rather than generic, because the person reading this failure is usually not the
            // person who wrote the module.
            throw new InvalidOperationException(
                $"Module '{_module.Name}' tried to configure the schema for '{typeof(T).FullName}', "
                + $"which ships in '{owner.GetName().Name}'. A module may only configure document "
                + $"types from its own assemblies ({string.Join(", ", _owned.Select(a => a.GetName().Name))}). "
                + "Configuring a type owned by core or by another module would change how that data "
                + "is stored for everyone.");
        }

        return _options.Schema.For<T>();
    }
}
