using Marten;
using Marten.Services;
using Npgsql;

namespace BarakoCMS.Tests;

/// <summary>
/// Holds each Marten session at its first statement that writes a document of the named table until
/// <paramref name="parties"/> arrivals have been counted, so a race can be forced at the save and
/// not at the reads before it. A test that is itself a party calls <see cref="Release"/> to let a
/// held session go. A session that waits past the timeout goes on anyway and <see cref="Reached"/>
/// stays false, so a stuck barrier fails the test and cannot hang it.
/// </summary>
internal sealed class WriteBarrierLogger(int parties, string table) : IMartenLogger
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private static readonly string[] Verbs = ["insert", "update", "upsert"];

    private readonly CountdownEvent _arrived = new(parties);
    private readonly ManualResetEventSlim _held = new(false);

    public bool Reached => _arrived.IsSet;

    /// <summary>Waits until one session stands at its write.</summary>
    public bool WaitUntilHeld() => _held.Wait(Timeout);

    /// <summary>Counts one arrival on behalf of the test.</summary>
    public void Release()
    {
        if (!_arrived.IsSet) _arrived.Signal();
    }

    public IMartenSessionLogger StartSession(IQuerySession session) => new Session(this);

    public void SchemaChange(string sql) { }

    private bool Matches(string sql) =>
        sql.Contains(table, StringComparison.OrdinalIgnoreCase)
        && Verbs.Any(v => sql.Contains(v, StringComparison.OrdinalIgnoreCase));

    private void Arrive()
    {
        if (!_arrived.IsSet) _arrived.Signal();
        _held.Set();
        _arrived.Wait(Timeout);
    }

    private sealed class Session(WriteBarrierLogger owner) : IMartenSessionLogger
    {
        private bool _done;

        private void Hold(string sql)
        {
            if (_done || !owner.Matches(sql)) return;
            _done = true;
            owner.Arrive();
        }

        public void LogSuccess(NpgsqlCommand command) { }
        public void LogFailure(NpgsqlCommand command, Exception ex) { }
        public void LogSuccess(NpgsqlBatch batch) { }
        public void LogFailure(NpgsqlBatch batch, Exception ex) { }
        public void LogFailure(Exception ex, string message) { }
        public void RecordSavedChanges(IDocumentSession session, IChangeSet commit) { }
        public void OnBeforeExecute(NpgsqlCommand command) => Hold(command.CommandText);

        public void OnBeforeExecute(NpgsqlBatch batch)
        {
            foreach (var command in batch.BatchCommands) Hold(command.CommandText);
        }
    }
}
