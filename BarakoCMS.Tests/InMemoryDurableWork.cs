using System.Text.Json;
using barakoCMS.Core.Interfaces;

namespace BarakoCMS.Tests;

/// <summary>One time a handler was given a message, and how it went.</summary>
/// <param name="Tenant">The tenant the message was queued for.</param>
/// <param name="MessageId">The message's id, the same on every delivery of it.</param>
/// <param name="Message">The message as the handler received it, read back from what was stored.</param>
/// <param name="At">What the clock said.</param>
/// <param name="Attempt">One for the first delivery, counting every delivery of this message after it.</param>
/// <param name="Error">What the handler threw, or null when it returned.</param>
public sealed record InMemoryDelivery(
    string Tenant, string MessageId, object Message, DateTimeOffset At, int Attempt, Exception? Error);

/// <summary>A committed message that has not been handled yet, or is waiting to be tried again.</summary>
public sealed record InMemoryPendingMessage(string Tenant, string MessageId, object Message, DateTimeOffset DueAt);

/// <summary>A message that failed <see cref="InMemoryDurableWork.MaxAttempts"/> times and will not be run again.</summary>
public sealed record InMemoryDeadLetter(string Tenant, string MessageId, object Message, string Reason);

/// <summary>A run parked through <see cref="IDurableRuns.WaitAsync"/> that has neither been resumed nor timed out.</summary>
public sealed record InMemoryWait(string Tenant, string WaitKey, DateTimeOffset TimeoutAt);

internal enum StagedKind
{
    Message,
    Start,
    Wait,
    Resume,
}

internal sealed record Staged(StagedKind Kind, string Tenant, string? Key, DateTimeOffset? DueAt, Type Type, string Json);

/// <summary>
/// The durable work seams in memory, for a test that wants to see what was queued and run it.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here runs on its own. There is no timer and no thread: the clock is <see cref="Now"/>,
/// it moves only when a test calls <see cref="AdvanceAsync"/> or <see cref="AdvanceToAsync"/>, and
/// handlers run inside <see cref="RunDueAsync"/>, one at a time, in due order and then in the
/// order they were committed. The same test gives the same result every time.
/// </para>
/// <para>
/// <see cref="Begin"/> opens a unit of work, which is what stands in for the session a caller
/// writes with. <see cref="InMemoryUnitOfWork.Commit"/> is its save. A unit of work that is
/// disposed without it leaves nothing behind.
/// </para>
/// <para>
/// What it keeps of the contract: a message is handled only after its unit of work commits, a
/// scheduled message is not handled before its time, a run id starts once and a wait key parks
/// once per tenant, a wait is resumed or timed out and never both, the handler is told the tenant
/// the caller named, a message is read back from JSON before it is handled, and a throwing handler
/// is tried again after <see cref="RetryDelay"/> until <see cref="MaxAttempts"/> and then dead
/// lettered.
/// </para>
/// <para>
/// What it cannot show: a crash, a restart, two nodes, or a handler outliving a time limit.
/// <see cref="Redeliver"/> stands in for those, by handing a handled message to its handler again.
/// It never forgets a run id or a wait key, where a host forgets them once finished work is
/// removed. Its retry wait is fixed, where a host backs off. It is not safe to use from two
/// threads.
/// </para>
/// </remarks>
public sealed class InMemoryDurableWork
{
    /// <summary>
    /// How many deliveries one call may make before it stops and says so. A handler that queues
    /// itself with no delay would otherwise never let the call return.
    /// </summary>
    public const int MaxDeliveriesPerCall = 10_000;

    private readonly Dictionary<Type, Func<object, DurableMessageContext, InMemoryUnitOfWork, CancellationToken, Task>> _handlers = new();
    private readonly List<Stored> _pending = new();
    private readonly Dictionary<string, Stored> _known = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Tenant, string Key), Parked> _waits = new();
    private readonly HashSet<(string Tenant, string Key)> _waitKeys = new();
    private readonly HashSet<(string Tenant, string RunId)> _runs = new();
    private readonly List<InMemoryDelivery> _delivered = new();
    private readonly List<InMemoryDeadLetter> _deadLetters = new();
    private readonly int _maxAttempts = 5;
    private readonly TimeSpan _retryDelay = TimeSpan.FromSeconds(30);
    private long _sequence;

    /// <param name="now">Where the clock starts. The machine's clock is never read.</param>
    public InMemoryDurableWork(DateTimeOffset now) => Now = now;

    /// <summary>The clock. It moves only through <see cref="AdvanceAsync"/> and <see cref="AdvanceToAsync"/>.</summary>
    public DateTimeOffset Now { get; private set; }

    /// <summary>How many times a message is handed to a handler that keeps throwing. Five unless set.</summary>
    public int MaxAttempts
    {
        get => _maxAttempts;
        init => _maxAttempts = value >= 1
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), "A message needs one attempt to run at all.");
    }

    /// <summary>How long after a throw the message is due again. Thirty seconds unless set, and never random.</summary>
    public TimeSpan RetryDelay
    {
        get => _retryDelay;
        init => _retryDelay = value >= TimeSpan.Zero
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), "The wait before a retry cannot be negative.");
    }

    /// <summary>Every delivery so far, failed ones included, in the order they ran.</summary>
    public IReadOnlyList<InMemoryDelivery> Delivered => _delivered.ToArray();

    /// <summary>What is committed and not yet handled, in the order it would run.</summary>
    public IReadOnlyList<InMemoryPendingMessage> Pending => _pending
        .OrderBy(m => m.DueAt)
        .ThenBy(m => m.Sequence)
        .Select(m => new InMemoryPendingMessage(m.Tenant, m.MessageId, m.Read(), m.DueAt))
        .ToArray();

    /// <summary>Messages that ran out of attempts.</summary>
    public IReadOnlyList<InMemoryDeadLetter> DeadLetters => _deadLetters.ToArray();

    /// <summary>Runs that are parked, soonest deadline first.</summary>
    public IReadOnlyList<InMemoryWait> Waits => _waits.Values
        .OrderBy(w => w.TimeoutAt)
        .ThenBy(w => w.Sequence)
        .Select(w => new InMemoryWait(w.Tenant, w.Key, w.TimeoutAt))
        .ToArray();

    /// <summary>The messages of one type whose handler returned, in the order they ran.</summary>
    public IReadOnlyList<TMessage> Handled<TMessage>()
        where TMessage : class =>
        _delivered.Where(d => d.Error is null).Select(d => d.Message).OfType<TMessage>().ToArray();

    /// <summary>
    /// Registers the handler for one message type.
    /// </summary>
    /// <remarks>
    /// The handler is given a unit of work opened for the message's tenant, which is what a scope
    /// bound to that tenant gives a real handler. Pass it to the class under test as its
    /// <see cref="IDurableOutbox"/> and <see cref="IDurableRuns"/>, and call
    /// <see cref="InMemoryUnitOfWork.Commit"/> where the real handler saves. Left uncommitted, what
    /// the handler staged is discarded, as the contract says.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The type already has a handler. The contract is one per type.</exception>
    public InMemoryDurableWork Handle<TMessage>(
        Func<TMessage, DurableMessageContext, InMemoryUnitOfWork, CancellationToken, Task> handler)
        where TMessage : class
    {
        ArgumentNullException.ThrowIfNull(handler);

        if (!_handlers.TryAdd(typeof(TMessage), (message, context, unit, ct) => handler((TMessage)message, context, unit, ct)))
        {
            throw new InvalidOperationException(
                $"A handler for {typeof(TMessage).Name} is already registered. The contract is one handler per message type.");
        }

        return this;
    }

    /// <summary>Opens a unit of work for one tenant. Nothing staged in it exists until it is committed.</summary>
    public InMemoryUnitOfWork Begin(string tenant)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        return new InMemoryUnitOfWork(this, tenant);
    }

    /// <summary>
    /// Runs everything that is due at <see cref="Now"/>, including what the handlers it runs commit
    /// as due, until nothing is.
    /// </summary>
    /// <returns>How many deliveries were made, failed ones included.</returns>
    /// <exception cref="InvalidOperationException">More than <see cref="MaxDeliveriesPerCall"/> deliveries were due.</exception>
    public Task<int> RunDueAsync(CancellationToken cancellationToken = default) =>
        RunDueAsync(0, cancellationToken);

    /// <summary>Moves the clock forward by <paramref name="by"/>, running what comes due on the way.</summary>
    /// <returns>How many deliveries were made, failed ones included.</returns>
    public Task<int> AdvanceAsync(TimeSpan by, CancellationToken cancellationToken = default)
    {
        if (by < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(by), "The clock only moves forward.");
        }

        return AdvanceToAsync(Now + by, cancellationToken);
    }

    /// <summary>
    /// Moves the clock forward to <paramref name="to"/>, stopping at each due time on the way.
    /// </summary>
    /// <remarks>
    /// The clock stands at a message's due time while its handler runs, not at
    /// <paramref name="to"/>, so a handler that schedules its own next occurrence from the time it
    /// was due behaves as it would if the time had really passed.
    /// </remarks>
    /// <returns>How many deliveries were made, failed ones included.</returns>
    public async Task<int> AdvanceToAsync(DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        if (to < Now)
        {
            throw new ArgumentOutOfRangeException(nameof(to), "The clock only moves forward.");
        }

        var ran = await RunDueAsync(0, cancellationToken);

        while (NextDue() is { } next && next <= to)
        {
            Now = next;
            ran = await RunDueAsync(ran, cancellationToken);
        }

        Now = to;
        return ran;
    }

    /// <summary>
    /// Makes a message whose handler already returned due again, with the same id.
    /// </summary>
    /// <remarks>
    /// This is the second delivery the contract allows after a crash, a redeploy or a lost lease.
    /// Call <see cref="RunDueAsync"/> afterwards. A handler that is not idempotent on the message id
    /// shows it here.
    /// </remarks>
    /// <exception cref="ArgumentException">No message with that id was ever committed.</exception>
    /// <exception cref="InvalidOperationException">The message is still pending or was dead lettered.</exception>
    public void Redeliver(string messageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);

        if (!_known.TryGetValue(messageId, out var stored))
        {
            throw new ArgumentException($"No message with the id '{messageId}' was committed.", nameof(messageId));
        }

        if (!stored.Handled)
        {
            throw new InvalidOperationException(
                $"The message '{messageId}' has not been handled, so there is no second delivery to make.");
        }

        stored.Handled = false;
        stored.DueAt = Now;
        _pending.Add(stored);
    }

    internal bool IsWaiting(string tenant, string waitKey) => _waits.ContainsKey((tenant, waitKey));

    /// <summary>Commits one unit of work, all of it or none of it.</summary>
    internal void Apply(IReadOnlyList<Staged> staged)
    {
        foreach (var resume in staged.Where(s => s.Kind == StagedKind.Resume))
        {
            if (!_waits.ContainsKey((resume.Tenant, resume.Key!)))
            {
                throw new InvalidOperationException(
                    $"The wait '{resume.Key}' was resumed or timed out before this unit of work committed, "
                    + "so nothing in the unit of work was committed.");
            }
        }

        foreach (var item in staged)
        {
            switch (item.Kind)
            {
                case StagedKind.Message:
                    Store(item.Tenant, item.Type, item.Json, item.DueAt ?? Now);
                    break;

                case StagedKind.Start:
                    if (_runs.Add((item.Tenant, item.Key!)))
                    {
                        Store(item.Tenant, item.Type, item.Json, Now);
                    }

                    break;

                case StagedKind.Wait:
                    if (_waitKeys.Add((item.Tenant, item.Key!)))
                    {
                        _waits[(item.Tenant, item.Key!)] =
                            new Parked(++_sequence, item.Tenant, item.Key!, item.DueAt!.Value, item.Type, item.Json);
                    }

                    break;

                case StagedKind.Resume:
                    _waits.Remove((item.Tenant, item.Key!));
                    Store(item.Tenant, item.Type, item.Json, Now);
                    break;
            }
        }
    }

    private async Task<int> RunDueAsync(int ran, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReleaseTimedOut();

            var next = _pending
                .Where(m => m.DueAt <= Now)
                .OrderBy(m => m.DueAt)
                .ThenBy(m => m.Sequence)
                .FirstOrDefault();

            if (next is null)
            {
                return ran;
            }

            if (ran >= MaxDeliveriesPerCall)
            {
                throw new InvalidOperationException(
                    $"{MaxDeliveriesPerCall} deliveries were made and more are still due. "
                    + "A handler is probably queueing itself with no delay.");
            }

            _pending.Remove(next);
            await DeliverAsync(next, cancellationToken);
            ran++;
        }
    }

    private async Task DeliverAsync(Stored stored, CancellationToken cancellationToken)
    {
        var message = stored.Read();
        stored.Attempts++;
        Exception? error = null;

        if (!_handlers.TryGetValue(stored.Type, out var handler))
        {
            error = new InvalidOperationException($"No handler is registered for {stored.Type.Name}.");
        }
        else
        {
            using var unit = Begin(stored.Tenant);

            try
            {
                var context = new DurableMessageContext { Tenant = stored.Tenant, MessageId = stored.MessageId };
                await handler(message, context, unit, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The test stopped the run. That is not an attempt, and the message is still owed.
                stored.Attempts--;
                _pending.Add(stored);
                throw;
            }
            catch (Exception ex)
            {
                error = ex;
            }
        }

        _delivered.Add(new InMemoryDelivery(stored.Tenant, stored.MessageId, message, Now, stored.Attempts, error));

        if (error is null)
        {
            stored.Handled = true;
            return;
        }

        if (stored.Attempts >= MaxAttempts)
        {
            _deadLetters.Add(new InMemoryDeadLetter(stored.Tenant, stored.MessageId, message, error.Message));
            return;
        }

        stored.DueAt = Now + RetryDelay;
        _pending.Add(stored);
    }

    private void ReleaseTimedOut()
    {
        var timedOut = _waits.Values
            .Where(w => w.TimeoutAt <= Now)
            .OrderBy(w => w.TimeoutAt)
            .ThenBy(w => w.Sequence)
            .ToList();

        foreach (var parked in timedOut)
        {
            _waits.Remove((parked.Tenant, parked.Key));
            Store(parked.Tenant, parked.Type, parked.Json, parked.TimeoutAt);
        }
    }

    private DateTimeOffset? NextDue()
    {
        DateTimeOffset? next = null;

        foreach (var due in _pending.Select(m => m.DueAt).Concat(_waits.Values.Select(w => w.TimeoutAt)))
        {
            if (next is null || due < next)
            {
                next = due;
            }
        }

        return next;
    }

    private void Store(string tenant, Type type, string json, DateTimeOffset dueAt)
    {
        var sequence = ++_sequence;

        var stored = new Stored
        {
            Sequence = sequence,
            Tenant = tenant,
            MessageId = $"message-{sequence}",
            Type = type,
            Json = json,
            DueAt = dueAt,
        };

        _known[stored.MessageId] = stored;
        _pending.Add(stored);
    }

    private sealed class Stored
    {
        public required long Sequence { get; init; }

        public required string Tenant { get; init; }

        public required string MessageId { get; init; }

        public required Type Type { get; init; }

        public required string Json { get; init; }

        public DateTimeOffset DueAt { get; set; }

        public int Attempts { get; set; }

        public bool Handled { get; set; }

        public object Read() =>
            JsonSerializer.Deserialize(Json, Type)
            ?? throw new InvalidOperationException($"The stored {Type.Name} read back as null.");
    }

    private sealed record Parked(long Sequence, string Tenant, string Key, DateTimeOffset TimeoutAt, Type Type, string Json);
}

/// <summary>
/// One unit of work against <see cref="InMemoryDurableWork"/>: the outbox and the runs a caller
/// would resolve from its scope, with <see cref="Commit"/> standing in for the save.
/// </summary>
/// <remarks>
/// It belongs to one tenant, the way a session does, and refuses work named for another. Disposing
/// it without committing discards what was staged, which is a request that threw or a save that
/// failed.
/// </remarks>
public sealed class InMemoryUnitOfWork : IDurableOutbox, IDurableRuns, IDisposable
{
    private readonly InMemoryDurableWork _work;
    private readonly List<Staged> _staged = new();
    private bool _closed;

    internal InMemoryUnitOfWork(InMemoryDurableWork work, string tenant)
    {
        _work = work;
        Tenant = tenant;
    }

    /// <summary>The tenant this unit of work was opened for.</summary>
    public string Tenant { get; }

    /// <summary>Whether <see cref="Commit"/> went through.</summary>
    public bool Committed { get; private set; }

    /// <inheritdoc />
    public Task EnqueueAsync<TMessage>(string tenant, TMessage message, CancellationToken cancellationToken)
        where TMessage : class
    {
        Stage(StagedKind.Message, tenant, key: null, dueAt: null, message, cancellationToken);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ScheduleAsync<TMessage>(string tenant, TMessage message, DateTimeOffset dueAt, CancellationToken cancellationToken)
        where TMessage : class
    {
        Stage(StagedKind.Message, tenant, key: null, dueAt, message, cancellationToken);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StartAsync<TMessage>(string tenant, string runId, TMessage message, CancellationToken cancellationToken)
        where TMessage : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        Stage(StagedKind.Start, tenant, runId, dueAt: null, message, cancellationToken);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task WaitAsync<TMessage>(string tenant, string waitKey, DateTimeOffset timeoutAt, TMessage onTimeout, CancellationToken cancellationToken)
        where TMessage : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(waitKey);
        Stage(StagedKind.Wait, tenant, waitKey, timeoutAt, onTimeout, cancellationToken);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> ResumeAsync<TMessage>(string tenant, string waitKey, TMessage message, CancellationToken cancellationToken)
        where TMessage : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(waitKey);
        Check(tenant, cancellationToken);
        ArgumentNullException.ThrowIfNull(message);

        var alreadyStaged = _staged.Any(s => s.Kind == StagedKind.Resume && string.Equals(s.Key, waitKey, StringComparison.Ordinal));

        if (alreadyStaged || !_work.IsWaiting(tenant, waitKey))
        {
            return Task.FromResult(false);
        }

        Stage(StagedKind.Resume, tenant, waitKey, dueAt: null, message, cancellationToken);
        return Task.FromResult(true);
    }

    /// <summary>
    /// Commits everything staged, all of it or none of it. This is the caller's save.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The unit of work was already committed or discarded, or a wait it resumes was resumed or
    /// timed out first. Nothing it staged is committed then.
    /// </exception>
    public void Commit()
    {
        EnsureOpen();

        try
        {
            _work.Apply(_staged);
            Committed = true;
        }
        finally
        {
            _closed = true;
            _staged.Clear();
        }
    }

    /// <summary>Discards what was staged, unless it was committed.</summary>
    public void Dispose()
    {
        _closed = true;
        _staged.Clear();
    }

    private void Stage<TMessage>(
        StagedKind kind, string tenant, string? key, DateTimeOffset? dueAt, TMessage message, CancellationToken cancellationToken)
        where TMessage : class
    {
        Check(tenant, cancellationToken);
        ArgumentNullException.ThrowIfNull(message);

        var type = message.GetType();
        _staged.Add(new Staged(kind, tenant, key, dueAt, type, JsonSerializer.Serialize(message, type)));
    }

    private void Check(string tenant, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureOpen();
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);

        if (!string.Equals(tenant, Tenant, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"This unit of work was opened for tenant '{Tenant}' and the call named '{tenant}'.", nameof(tenant));
        }
    }

    private void EnsureOpen()
    {
        if (_closed)
        {
            throw new InvalidOperationException("This unit of work was already committed or discarded.");
        }
    }
}
