namespace barakoCMS.Infrastructure.Jobs;

/// <summary>
/// Holds the job queue workers off storage until the schema is in place.
/// </summary>
/// <remarks>
/// <para>
/// <c>UseBarakoCMS</c> starts the workers, and every host calls it before
/// <c>ApplyMartenSchemaAsync</c>. A worker polls as soon as it starts, and on a database without the
/// jobs table Marten creates the table and its indexes right then, lazily and outside
/// <c>SchemaApplyLock</c>. The explicit apply beside it sees the same indexes missing, and the second
/// of the two to issue a <c>CREATE INDEX</c> fails on
/// <c>42P07: relation "mt_doc_jobs_idx_queue_id" already exists</c>. See issue #686.
/// </para>
/// <para>
/// Opened when <c>ApplyMartenSchemaAsync</c> finishes, and in any case when the host has started, so a
/// host that assembles its own startup without the explicit apply keeps its workers.
/// </para>
/// </remarks>
internal sealed class JobStorageGate
{
    private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsOpen => _open.Task.IsCompleted;

    public void Open() => _open.TrySetResult();

    public Task WaitAsync(CancellationToken ct) => _open.Task.WaitAsync(ct);
}
