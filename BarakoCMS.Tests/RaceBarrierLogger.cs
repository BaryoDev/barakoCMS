using Marten;
using Marten.Services;
using Npgsql;

namespace BarakoCMS.Tests;

/// <summary>
/// Holds each Marten session at its first statement that mentions one of the markers until the
/// expected number of sessions have reached theirs, so racing requests all stand at the same point
/// before any goes on. A session that waits past the timeout goes on anyway and
/// <see cref="Reached"/> stays false, so a stuck barrier fails the test and cannot hang it.
/// </summary>
internal sealed class RaceBarrierLogger(int parties, params string[] markers) : IMartenLogger
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly CountdownEvent _arrived = new(parties);

    public bool Reached => _arrived.IsSet;

    public IMartenSessionLogger StartSession(IQuerySession session) => new Session(this);

    public void SchemaChange(string sql) { }

    private bool Matches(string sql) =>
        markers.Any(m => sql.Contains(m, StringComparison.OrdinalIgnoreCase));

    private void Arrive()
    {
        if (!_arrived.IsSet) _arrived.Signal();
        _arrived.Wait(Timeout);
    }

    private sealed class Session(RaceBarrierLogger owner) : IMartenSessionLogger
    {
        private bool _held;

        private void Hold(string sql)
        {
            if (_held || !owner.Matches(sql)) return;
            _held = true;
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
