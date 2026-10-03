using Marten;

namespace barakoCMS.Infrastructure.Tracing;

/// <summary>
/// Puts the correlation id and the cause on every event a session is about to store, whichever
/// way the session was opened.
/// </summary>
/// <remarks>
/// Registered on the store, so it runs for a session a module opened straight from
/// <see cref="IDocumentStore"/> as well as for the scoped one. It has to be here and not where
/// sessions are built: Marten stamps every session it opens with <c>Activity.Current.RootId</c> and
/// <c>Activity.Current.ParentId</c>, and the parent id is the caller's <c>traceparent</c> or
/// <c>Request-Id</c> header exactly as sent. Left alone, a session opened outside the scoped
/// factory would store that header on its events.
///
/// It runs after Marten has copied the session's values onto the events and before any statement
/// is built, and it overwrites both. The correlation id is the ambient one, see
/// <see cref="Correlation"/>, or else what Marten put there when that is a valid id, which is the
/// trace id of a W3C span. The cause is always <see cref="Correlation.Cause"/>: the span doing the
/// write, or what the scope fixed. Anything that fails the check is stored as null.
/// </remarks>
internal sealed class EventOriginListener : DocumentSessionListenerBase
{
    public override Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken token)
    {
        var streams = session.PendingChanges.Streams();
        if (streams.Count == 0) return Task.CompletedTask;

        var ambient = Correlation.Id;
        var cause = Correlation.Cause;

        foreach (var stream in streams)
        {
            foreach (var @event in stream.Events)
            {
                @event.CorrelationId = ambient ?? Correlation.Normalise(@event.CorrelationId);
                @event.CausationId = cause;
            }
        }

        return Task.CompletedTask;
    }
}
