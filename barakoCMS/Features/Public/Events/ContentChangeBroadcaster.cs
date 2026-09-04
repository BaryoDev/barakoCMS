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

    /// <summary>
    /// Registers a connection, or returns null when the instance is already at
    /// <paramref name="maxConnections"/>. The slot is taken before the check so two connections
    /// arriving together cannot both squeeze past the cap.
    /// </summary>
    public Subscription? TrySubscribe(string tenant, IReadOnlyCollection<string> contentTypes, int maxConnections)
    {
        if (Interlocked.Increment(ref _connections) > maxConnections)
        {
            Interlocked.Decrement(ref _connections);
            return null;
        }

        var subscription = new Subscription(this, tenant, contentTypes, _logger);
        _tenants.GetOrAdd(tenant, _ => new ConcurrentDictionary<Guid, Subscription>())
            .TryAdd(subscription.Id, subscription);
        return subscription;
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
            ILogger logger)
        {
            _owner = owner;
            _logger = logger;
            Tenant = tenant;
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
