using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace barakoCMS.Features.Public.Events;

/// <summary>
/// Fans a tenant's content changes out to the connections subscribed to that tenant, in process.
/// </summary>
/// <remarks>
/// Keyed by tenant slug, and that key is the isolation: a change is offered only to the
/// subscriptions registered under the tenant the write committed in, so a subscriber on one tenant
/// never holds a channel another tenant's change is written to.
///
/// In process, and only in process. With several API instances, each streams the writes it handled
/// and nothing else, because there is no shared bus between them yet. docs/delivery-api.md says so
/// in those words.
///
/// One bounded channel per connection. A subscriber that stops reading does not hold memory for
/// everyone else: once its buffer is full the oldest change is dropped, and the drop is logged once
/// per connection rather than once per change.
/// </remarks>
internal sealed class ContentChangeBroadcaster
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Subscription>> _tenants =
        new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, int> _perClient = new(StringComparer.Ordinal);

    private readonly ILogger<ContentChangeBroadcaster> _logger;
    private int _connections;

    public ContentChangeBroadcaster(ILogger<ContentChangeBroadcaster> logger) => _logger = logger;

    /// <summary>Open connections across every tenant on this instance.</summary>
    public int Connections => Volatile.Read(ref _connections);

    /// <summary>
    /// Whether any connection is listening on a tenant, so a write path can skip the projection
    /// work entirely when the answer is no. That is the common case on every deployment that has
    /// not turned the stream on.
    /// </summary>
    public bool HasSubscribers(string tenant) =>
        _tenants.TryGetValue(tenant, out var subscriptions) && !subscriptions.IsEmpty;

    /// <summary>Open connections from one client address on this instance.</summary>
    public int ConnectionsFor(string client) => _perClient.GetValueOrDefault(client);

    /// <summary>
    /// Registers a connection, or returns null with <paramref name="refusal"/> saying which cap
    /// was hit. Each slot is taken before its check so two connections arriving together cannot
    /// both squeeze past a cap. The per-client cap is checked first, so a caller that is over its
    /// own limit is told so even when the instance is full too; <paramref name="maxPerClient"/>
    /// of zero skips it.
    /// </summary>
    public Subscription? TrySubscribe(
        string tenant,
        IReadOnlyCollection<string> contentTypes,
        string client,
        int maxConnections,
        int maxPerClient,
        out SubscribeRefusal refusal)
    {
        refusal = SubscribeRefusal.None;

        if (maxPerClient > 0 && _perClient.AddOrUpdate(client, 1, (_, n) => n + 1) > maxPerClient)
        {
            ReleaseClient(client);
            refusal = SubscribeRefusal.ClientCap;
            return null;
        }

        if (Interlocked.Increment(ref _connections) > maxConnections)
        {
            Interlocked.Decrement(ref _connections);
            if (maxPerClient > 0)
            {
                ReleaseClient(client);
            }

            refusal = SubscribeRefusal.InstanceCap;
            return null;
        }

        var subscription = new Subscription(this, tenant, contentTypes, maxPerClient > 0 ? client : null, _logger);
        _tenants.GetOrAdd(tenant, _ => new ConcurrentDictionary<Guid, Subscription>())
            .TryAdd(subscription.Id, subscription);
        return subscription;
    }

    /// <summary>
    /// Gives a client's slot back and drops the entry once it reaches zero, so the dictionary
    /// holds only addresses with an open stream. The remove is conditional on the value still
    /// being zero, which is what keeps a connection arriving in between from losing its count.
    /// </summary>
    private void ReleaseClient(string client)
    {
        var remaining = _perClient.AddOrUpdate(client, 0, (_, n) => n - 1);
        if (remaining <= 0)
        {
            ((ICollection<KeyValuePair<string, int>>)_perClient).Remove(new KeyValuePair<string, int>(client, remaining));
        }
    }

    /// <summary>Offers a change to every connection on its tenant that wants its content type.</summary>
    public void Publish(string tenant, ContentChange change)
    {
        if (!_tenants.TryGetValue(tenant, out var subscriptions))
        {
            return;
        }

        foreach (var subscription in subscriptions.Values)
        {
            if (subscription.Accepts(change.ContentType))
            {
                subscription.Offer(change);
            }
        }
    }

    private void Remove(Subscription subscription)
    {
        if (_tenants.TryGetValue(subscription.Tenant, out var subscriptions))
        {
            subscriptions.TryRemove(subscription.Id, out _);
        }

        Interlocked.Decrement(ref _connections);
        if (subscription.Client is not null)
        {
            ReleaseClient(subscription.Client);
        }
    }

    /// <summary>Why <see cref="TrySubscribe"/> said no.</summary>
    internal enum SubscribeRefusal
    {
        None,
        InstanceCap,
        ClientCap,
    }

    /// <summary>One connection's view of the stream. Dispose it when the connection ends.</summary>
    internal sealed class Subscription : IDisposable
    {
        private readonly ContentChangeBroadcaster _owner;
        private readonly HashSet<string>? _contentTypes;
        private readonly Channel<ContentChange> _channel;
        private readonly ILogger _logger;
        private int _droppedLogged;
        private int _disposed;

        internal Subscription(
            ContentChangeBroadcaster owner,
            string tenant,
            IReadOnlyCollection<string> contentTypes,
            string? client,
            ILogger logger)
        {
            _owner = owner;
            _logger = logger;
            Tenant = tenant;
            Client = client;
            _contentTypes = contentTypes.Count == 0
                ? null
                : new HashSet<string>(contentTypes, StringComparer.OrdinalIgnoreCase);
            _channel = Channel.CreateBounded<ContentChange>(
                new BoundedChannelOptions(ContentEventsOptions.BufferPerConnection)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false,
                },
                Dropped);
        }

        public Guid Id { get; } = Guid.NewGuid();

        public string Tenant { get; }

        /// <summary>The address counted against the per-client cap, or null when that cap is off.</summary>
        public string? Client { get; }

        public ChannelReader<ContentChange> Reader => _channel.Reader;

        public bool Accepts(string contentType) =>
            _contentTypes is null || _contentTypes.Contains(contentType);

        internal void Offer(ContentChange change) => _channel.Writer.TryWrite(change);

        private void Dropped(ContentChange dropped)
        {
            if (Interlocked.Exchange(ref _droppedLogged, 1) == 0)
            {
                _logger.LogWarning(
                    "Event stream subscriber {SubscriptionId} on tenant {Tenant} is not keeping up; "
                    + "dropping the oldest change ({EventName} {ContentId}). Further drops on this connection are not logged.",
                    Id, Tenant, dropped.EventName, dropped.Id);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            _owner.Remove(this);
            _channel.Writer.TryComplete();
        }
    }
}
