using System.Collections.Concurrent;
using Prometheus;

namespace barakoCMS.Infrastructure.Auth;

internal enum ApiKeyRefusalReason
{
    Scope,
    ContentType,
    Expired,
    Revoked,
}

internal sealed record ApiKeyRefusal(DateTime At, ApiKeyRefusalReason Reason, string? ContentType);

/// <summary>
/// A rolling record of why each API key was refused, kept in memory. A refusal happens on a path
/// anyone can reach with a bad key, so nothing here writes to the database: the cost of a refusal
/// stays a few allocations. Bounds: only a key that exists is counted (a secret that matches no key
/// has no id and is never recorded), at most <see cref="MaxKeys"/> keys are tracked, at most
/// <see cref="MaxPerKey"/> refusals are kept per key, and anything older than <see cref="Window"/>
/// is dropped. Only the key id, the reason and a content type name (cut to
/// <see cref="MaxContentTypeLength"/>) are kept. Never the secret, a body or a header.
/// The counts live in one process, so they restart empty and a second replica has its own.
/// </summary>
internal sealed class ApiKeyRefusals(TimeProvider clock)
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);
    public const int MaxPerKey = 200;
    public const int MaxKeys = 10_000;
    public const int MaxContentTypeLength = 100;

    private static readonly Counter RefusalCounter = Metrics.CreateCounter(
        "barakocms_api_key_refusals_total",
        "Requests refused for an API key that exists, by reason. /metrics is anonymous, so no key id.",
        new CounterConfiguration { LabelNames = ["reason"] });

    private readonly ConcurrentDictionary<Guid, Queue<ApiKeyRefusal>> _byKey = new();

    public static string ReasonName(ApiKeyRefusalReason reason) => reason switch
    {
        ApiKeyRefusalReason.Scope => "scope",
        ApiKeyRefusalReason.ContentType => "content_type",
        ApiKeyRefusalReason.Expired => "expired",
        _ => "revoked",
    };

    public void Record(Guid keyId, ApiKeyRefusalReason reason, string? contentType = null)
    {
        RefusalCounter.WithLabels(ReasonName(reason)).Inc();

        if (contentType is { Length: > MaxContentTypeLength })
            contentType = contentType[..MaxContentTypeLength];
        var now = clock.GetUtcNow().UtcDateTime;
        if (!_byKey.ContainsKey(keyId) && _byKey.Count >= MaxKeys)
        {
            SweepExpired(now);
            if (_byKey.Count >= MaxKeys)
                return;
        }

        while (!TryAdd(keyId, new ApiKeyRefusal(now, reason, contentType), now)) { }
    }

    // False when a sweep removed the queue between the lookup and the lock; the caller looks again.
    private bool TryAdd(Guid keyId, ApiKeyRefusal refusal, DateTime now)
    {
        var queue = _byKey.GetOrAdd(keyId, _ => new Queue<ApiKeyRefusal>());
        lock (queue)
        {
            if (!_byKey.TryGetValue(keyId, out var current) || !ReferenceEquals(current, queue))
                return false;
            Trim(queue, now);
            while (queue.Count >= MaxPerKey)
                queue.Dequeue();
            queue.Enqueue(refusal);
            return true;
        }
    }

    /// <summary>The refusals still inside the window, oldest first.</summary>
    public IReadOnlyList<ApiKeyRefusal> Recent(Guid keyId)
    {
        if (!_byKey.TryGetValue(keyId, out var queue))
            return [];
        lock (queue)
        {
            Trim(queue, clock.GetUtcNow().UtcDateTime);
            return [.. queue];
        }
    }

    /// <summary>Keys with an entry in the window. Idle keys are only dropped when the cap is reached.</summary>
    internal int TrackedKeys => _byKey.Count;

    // Removing under the queue's lock means a Record that already holds the queue either finished
    // first (so it is not empty) or runs after and finds its queue gone, and adds a fresh one.
    private void SweepExpired(DateTime now)
    {
        foreach (var (id, queue) in _byKey)
        {
            lock (queue)
            {
                Trim(queue, now);
                if (queue.Count == 0)
                    _byKey.TryRemove(new KeyValuePair<Guid, Queue<ApiKeyRefusal>>(id, queue));
            }
        }
    }

    private static void Trim(Queue<ApiKeyRefusal> queue, DateTime now)
    {
        while (queue.Count > 0 && queue.Peek().At < now - Window)
            queue.Dequeue();
    }
}
