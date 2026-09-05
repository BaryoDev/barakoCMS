using System.Runtime.CompilerServices;
using System.Threading.Channels;
using FastEndpoints;

namespace barakoCMS.Features.Public.Events;

/// <summary>
/// GET /api/public/events: a server-sent event stream of the tenant's content changes.
/// Anonymous like the rest of public delivery, and answering to the same rules: only entries of a
/// publicly deliverable type, only while Published, only the fields the type marks Public.
/// </summary>
/// <remarks>
/// Off unless <c>Delivery:Events:Enabled</c> is true, and 404 while it is off, the same answer
/// an OAuth provider gives when it is not configured. The literal <c>events</c> segment wins over
/// <c>/api/public/{type}</c>, so a content type named "events" is not reachable by that route.
///
/// <c>?type=</c> repeats to filter by content type. A type that is not deliverable simply never
/// matches, which says nothing about whether it exists.
/// </remarks>
internal sealed class StreamEndpoint : EndpointWithoutRequest
{
    /// <summary>A filter is a handful of names, not a way to make the server hold a large set per connection.</summary>
    private const int MaxTypes = 20;

    private readonly ContentChangeBroadcaster _broadcaster;
    private readonly ContentEventsOptions _options;
    private readonly barakoCMS.Infrastructure.Multitenancy.TenantContext _tenant;

    public StreamEndpoint(
        ContentChangeBroadcaster broadcaster,
        ContentEventsOptions options,
        barakoCMS.Infrastructure.Multitenancy.TenantContext tenant)
    {
        _broadcaster = broadcaster;
        _options = options;
        _tenant = tenant;
    }

    public override void Configure()
    {
        Get("/api/public/events");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        // A stream, never a cached response: no-store outright, not the short max-age the rest of
        // Features/Public uses. Registered on OnStarting, not set directly, because
        // Send.EventStreamAsync (FastEndpoints) sets its own Cache-Control: no-cache on the success
        // path after this method runs, which would otherwise overwrite ours; OnStarting callbacks
        // fire immediately before headers actually go out, so this one runs last regardless of what
        // set the header earlier. Applies to every outcome alike (404 while off, 400 on too many
        // types, 503 at a connection cap, and the stream itself), since it fires whenever headers
        // are about to be sent, not only for a 200. Vary: X-Tenant goes out too, for consistency
        // with the rest of the surface, though a cache that honours no-store never needs it. See
        // docs/delivery-api.md for what a proxy in front of this route has to be configured to do,
        // which is more than "do not cache".
        HttpContext.Response.OnStarting(() =>
        {
            HttpContext.Response.Headers.CacheControl = "no-store";
            HttpContext.Response.Headers.Vary = barakoCMS.Infrastructure.Multitenancy.TenantResolutionMiddleware.TenantHeader;
            return Task.CompletedTask;
        });

        if (!_options.Enabled)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var types = HttpContext.Request.Query["type"]
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (types.Count > MaxTypes)
        {
            AddError($"Too many types. At most {MaxTypes} are allowed per connection.");
            await Send.ErrorsAsync(400, ct);
            return;
        }

        // The same address the rate limiter partitions on: the socket peer, already rewritten to
        // the forwarded client when ForwardedHeaders names the proxy. Not read from the header
        // here, for the reason DeviceContext gives.
        var client = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        using var subscription = _broadcaster.TrySubscribe(
            _tenant.Slug, types, client, _options.MaxConnections, _options.MaxConnectionsPerClient, out var refusal);
        if (subscription is null)
        {
            HttpContext.Response.Headers.RetryAfter = "5";
            var reason = refusal == ContentChangeBroadcaster.SubscribeRefusal.ClientCap
                ? $"This client is at its event stream connection limit ({_options.MaxConnectionsPerClient}). Close a stream before opening another."
                : $"The event stream is at its connection limit ({_options.MaxConnections}). Try again later.";
            await Send.StringAsync(reason, 503, "text/plain; charset=utf-8", ct);
            return;
        }

        await Send.EventStreamAsync(Stream(subscription, ct), ct);
    }

    /// <summary>
    /// Reads the connection's channel. Whenever nothing arrives within the configured interval a
    /// comment line goes out instead, so a proxy does not close an idle connection.
    /// </summary>
    /// <remarks>
    /// The comment is written to the response directly rather than yielded: the FastEndpoints
    /// writer emits every item as an <c>event:</c> plus <c>data:</c> pair and has no comment frame,
    /// and a comment is what the SSE spec gives a keepalive so that an <c>EventSource</c> never
    /// dispatches it. The writer flushes before it waits on this enumerator, so nothing of its own
    /// is pending when the comment is written.
    ///
    /// A linked token per read rather than a timeout on a shared read task: an abandoned
    /// <c>ReadAsync</c> stays registered on the channel and would consume the next change, which
    /// the following read would never see. Cancelling the read unregisters it, and a read that
    /// already dequeued an item completes with the item rather than the cancellation.
    /// </remarks>
    private async IAsyncEnumerable<StreamItem> Stream(
        ContentChangeBroadcaster.Subscription subscription,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var reader = subscription.Reader;

        while (!ct.IsCancellationRequested)
        {
            ContentChange? change = null;
            var closed = false;

            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(_options.KeepAlive);
                try
                {
                    change = await reader.ReadAsync(timeout.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // The interval passed with nothing to send.
                }
                catch (OperationCanceledException)
                {
                    closed = true;
                }
                catch (ChannelClosedException)
                {
                    closed = true;
                }
            }

            if (closed)
            {
                yield break;
            }

            if (change is null)
            {
                await HttpContext.Response.WriteAsync(ContentChangeEvents.KeepAliveComment, ct);
                await HttpContext.Response.Body.FlushAsync(ct);
                continue;
            }

            yield return new StreamItem(change.EventName, change.Payload);
        }
    }
}
