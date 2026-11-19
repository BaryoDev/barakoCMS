using System.Runtime.CompilerServices;
using Marten;

namespace BarakoCMS.Tests;

/// <summary>
/// Holds each Marten session at its first save that writes a document of type <typeparamref name="T"/>
/// until <paramref name="parties"/> arrivals have been counted, so a race can be forced at the save
/// and not at the reads before it. It reads the session's pending changes, so it does not depend on
/// the SQL Marten writes. A test that is itself a party calls <see cref="Release"/> to let a held
/// session go. A session that waits past the timeout goes on anyway and <see cref="Reached"/> stays
/// false, so a stuck barrier fails the test and cannot hang it.
/// </summary>
internal sealed class WriteBarrierListener<T>(int parties) : DocumentSessionListenerBase where T : class
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly CountdownEvent _arrived = new(parties);
    private readonly ManualResetEventSlim _held = new(false);
    private readonly ConditionalWeakTable<IDocumentSession, object> _seen = new();

    public bool Reached => _arrived.IsSet;

    /// <summary>Waits until one session stands at its save.</summary>
    public bool WaitUntilHeld() => _held.Wait(Timeout);

    /// <summary>Counts one arrival on behalf of the test.</summary>
    public void Release()
    {
        if (!_arrived.IsSet) _arrived.Signal();
    }

    public override async Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken token)
    {
        if (!session.PendingChanges.AllChangedFor<T>().Any()) return;
        if (!_seen.TryAdd(session, this)) return;

        if (!_arrived.IsSet) _arrived.Signal();
        _held.Set();
        await Task.Run(() => _arrived.Wait(Timeout), token);
    }
}
