using System.Reflection;
using System.Text;
using JasperFx.Events.Projections;
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
        Own(typeof(T), "configure the schema for", "configure document types");
        return _options.Schema.For<T>();
    }

    public void AddEventType<TEvent>() where TEvent : class
    {
        Own(typeof(TEvent), "register the event type", "register event types");
        _options.Events.MapEventType<TEvent>(EventAlias(_module.Name, typeof(TEvent)));
    }

    public void AddProjection<TProjection>(ProjectionLifecycle lifecycle)
        where TProjection : ProjectionBase, IProjectionSource<IDocumentOperations, IQuerySession>, new()
    {
        Own(typeof(TProjection), "add the projection", "add projections");

        // The projection being ours is not enough: a projection writes a document, and one that
        // writes core's User would re-map it as surely as For<User>() would.
        if (ProjectedDocument(typeof(TProjection)) is { } document)
            Own(document, "project into", "project into document types");

        _options.Projections.Add<TProjection>(lifecycle);
    }

    private void Own(Type type, string attempt, string rule)
    {
        var owner = type.Assembly;
        if (_owned.Contains(owner)) return;

        // Named rather than generic, because the person reading this failure is usually not the
        // person who wrote the module.
        throw new InvalidOperationException(
            $"Module '{_module.Name}' tried to {attempt} '{type.FullName}', "
            + $"which ships in '{owner.GetName().Name}'. A module may only {rule} "
            + $"from its own assemblies ({string.Join(", ", _owned.Select(a => a.GetName().Name))}). "
            + "Configuring a type owned by core or by another module would change how that data "
            + "is stored for everyone.");
    }

    /// <summary>
    /// <c>{module}.{event}</c>: the module name in kebab case, the event class name in snake case.
    /// </summary>
    internal static string EventAlias(string moduleName, Type eventType) =>
        $"{Words(moduleName, '-')}.{Words(eventType.Name, '_')}";

    private static string Words(string name, char separator)
    {
        var sb = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (!char.IsLetterOrDigit(c))
            {
                if (sb.Length > 0 && sb[^1] != separator) sb.Append(separator);
                continue;
            }

            // A capital starts a word after a lower case letter or digit ("ticketIssued"), and
            // ends an acronym when a lower case letter follows it ("HTTPTools").
            var startsWord = char.IsUpper(c) && sb.Length > 0 && sb[^1] != separator
                && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1])
                    || (char.IsUpper(name[i - 1]) && i + 1 < name.Length && char.IsLower(name[i + 1])));
            if (startsWord) sb.Append(separator);

            sb.Append(char.ToLowerInvariant(c));
        }

        return sb.ToString().Trim(separator);
    }

    /// <summary>
    /// The document a single or multi stream projection writes, or null for a projection shape
    /// that does not name one in its base type.
    /// </summary>
    internal static Type? ProjectedDocument(Type projection)
    {
        for (var t = projection; t is not null && t != typeof(object); t = t.BaseType)
        {
            if (!t.IsGenericType) continue;

            // By name, so a Marten namespace move does not quietly turn this check off.
            var name = t.GetGenericTypeDefinition().Name;
            if (name is "SingleStreamProjection`2" or "MultiStreamProjection`2")
                return t.GetGenericArguments()[0];
        }

        return null;
    }
}
