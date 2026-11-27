using Marten;

namespace barakoCMS.Infrastructure.Jobs;

/// <summary>
/// Names the session the next job is staged on, for a caller that writes with a session other than
/// the request's own.
/// </summary>
/// <remarks>
/// <c>QueueJobAsync</c> carries no session, so the job store stages on the request's scoped session
/// unless told otherwise. A caller on a tenant session or a batch scope's session wraps its queue
/// call in <c>using (JobEnqueue.On(session))</c>, and the job commits when that session does.
/// </remarks>
internal static class JobEnqueue
{
    private static readonly AsyncLocal<IDocumentSession?> Ambient = new();

    /// <summary>The session named by the innermost <see cref="On"/>, or null.</summary>
    internal static IDocumentSession? Current => Ambient.Value;

    /// <summary>Stages jobs queued inside the returned scope on <paramref name="session"/>.</summary>
    internal static IDisposable On(IDocumentSession session)
    {
        var previous = Ambient.Value;
        Ambient.Value = session;
        return new Restore(previous);
    }

    private sealed class Restore(IDocumentSession? previous) : IDisposable
    {
        public void Dispose() => Ambient.Value = previous;
    }
}
