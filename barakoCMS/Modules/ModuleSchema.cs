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

        // The module's own assembly, plus any it declares for endpoints. A module that splits its
        // models into a second assembly it ships is legitimate; one reaching into barakoCMS.dll or
        // another module's assembly is not.
        _owned = new HashSet<Assembly> { module.GetType().Assembly };
        foreach (var asm in module.EndpointAssemblies)
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
