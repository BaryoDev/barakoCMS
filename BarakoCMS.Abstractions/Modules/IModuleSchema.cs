using JasperFx.Events.Projections;
using Marten;
using Marten.Events.Projections;

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

    /// <summary>
    /// Register one of the module's own event types. Stored under the alias
    /// <c>{module}.{event}</c>, for example <c>queue.ticket_issued</c>, so two modules can each
    /// have a <c>TicketIssued</c> and the name a client sees in the change feed does not move when
    /// the class is renamed or moved to another namespace.
    /// </summary>
    /// <exception cref="InvalidOperationException">The type belongs to core or to another module.</exception>
    void AddEventType<TEvent>() where TEvent : class =>
        throw new NotSupportedException(
            "This host does not let modules register event types. It needs barakoCMS 4.9 or later.");

    /// <summary>
    /// Register one of the module's own projections. The projection and the document it writes must
    /// both come from the module's own assemblies.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The projection, or the document it projects into, belongs to core or to another module.
    /// </exception>
    void AddProjection<TProjection>(ProjectionLifecycle lifecycle)
        where TProjection : ProjectionBase, IProjectionSource<IDocumentOperations, IQuerySession>, new() =>
        throw new NotSupportedException(
            "This host does not let modules register projections. It needs barakoCMS 4.9 or later.");

    /// <summary>
    /// Store the headers a caller sets on an event (<c>session.Events.Append(...)</c> then
    /// <c>SetHeader</c>) in the event's <c>headers</c> column. The setting belongs to the event
    /// store, which every module shares, so it is on for all modules once any module asks for it.
    /// Calling it twice is harmless.
    /// </summary>
    /// <remarks>
    /// Adds a <c>headers</c> column to <c>mt_events</c> and changes <c>mt_quick_append_events</c>, so
    /// a database that already exists needs the matching migration before the host starts.
    /// </remarks>
    void EnableEventHeaders() =>
        throw new NotSupportedException(
            "This host does not let modules enable event headers. It needs a barakoCMS build that has IModuleSchema.EnableEventHeaders.");
}
