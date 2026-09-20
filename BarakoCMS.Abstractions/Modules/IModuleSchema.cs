using Marten;

namespace barakoCMS.Modules;

/// <summary>
/// The slice of Marten's schema a module is allowed to configure: its own document types, and
/// nothing else.
/// </summary>
/// <remarks>
/// Replaces handing modules the raw <see cref="StoreOptions"/>, which is the same instance core
/// configured. With that, a module could re-map <c>Content</c>, change tenancy, add an index to
/// <c>mt_doc_contents</c> or alter the event store, and neither core nor the operator would know.
///
/// The restriction is ownership, not a whitelist: a module may configure any type from an assembly
/// it ships. That covers a module which keeps its models in a separate assembly, while still
/// refusing anything belonging to core or to another module.
///
/// Narrowed while every caller was first-party. Once a third-party module has been written against
/// the wider surface, taking it back costs a major version.
/// </remarks>
public interface IModuleSchema
{
    /// <summary>
    /// Configure one of the module's own document types. Returns Marten's own mapping expression,
    /// so <c>.Index(...)</c> and the rest chain exactly as before.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The type belongs to core or to another module.
    /// </exception>
    MartenRegistry.DocumentMappingExpression<T> For<T>();
}
