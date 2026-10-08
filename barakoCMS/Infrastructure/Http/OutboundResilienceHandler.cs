using System.Net;

namespace barakoCMS.Infrastructure.Http;

/// <summary>
/// The retry, timeout and per-host breaker on the <c>ExternalApi</c> client, which webhooks,
/// connector requests and the other calls a workflow can aim go through.
/// </summary>
/// <remarks>
/// What is retried, and why:
///
/// - A connection that could not be opened, or a name that did not resolve, for any method:
///   nothing reached the server.
/// - 408, 429 and 503, for any method: the server said it did not process the request. A
///   <c>Retry-After</c> up to <see cref="OutboundResilienceOptions.MaxRetryAfter"/> is waited for;
///   a longer one ends the tries and the answer goes back to the caller.
/// - A timeout, 502, 504 or a connection lost mid-exchange only for a request that is safe to send
///   twice: an idempotent method, an <c>Idempotency-Key</c> header, or <see cref="Replayable"/>
///   set by the caller. Each of these may have reached the server, and a POST resent blind is a
///   second order or a second message.
///
/// Never retried: any other status, an address the guard refused, a TLS failure, or a body that
/// cannot be sent twice. The last answer is returned as it came, so a caller reading the status
/// still sees the 503 after the tries are spent.
/// </remarks>
internal sealed class OutboundResilienceHandler(OutboundResilience resilience) : DelegatingHandler
{
    /// <summary>
    /// Set by a caller whose receiver can recognise a resend, as a webhook can by its
    /// <c>X-Barako-Delivery</c> id, which is the same on every try.
    /// </summary>
    public static readonly HttpRequestOptionsKey<bool> Replayable = new("barako.outbound.replayable");

    public const string Scope = "http";

    /// <summary>The largest body of unknown kind buffered so it can be sent again.</summary>
    private const long MaxBufferedBody = 1024 * 1024;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is not { IsAbsoluteUri: true } uri)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        var options = resilience.Options;
        var retries = await CanSendBodyAgainAsync(request.Content) ? options.Retries : 0;
        var replayable = IsReplayable(request);
        HttpResponseMessage? held = null;

        try
        {
            return await resilience.RunAsync(
                Scope,
                uri.IdnHost.ToLowerInvariant(),
                retries,
                options.AttemptTimeout,
                async (number, token) =>
                {
                    held?.Dispose();
                    held = null;

                    var response = await base.SendAsync(request, token);
                    if (!IsTransient(response.StatusCode)) return response;

                    held = response;
                    var retry = replayable || WasNotProcessed(response.StatusCode);

                    if (retry && number <= retries && RetryAfter(response) is { } wait)
                    {
                        if (wait > options.MaxRetryAfter)
                        {
                            retry = false;
                        }
                        else
                        {
                            // The outer token, not the try's: the wait is the provider's request,
                            // not part of the try, and the budget counts it separately.
                            await Task.Delay(wait, cancellationToken);
                        }
                    }

                    throw new TransientResponseException(response, retry);
                },
                ex => ShouldRetry(ex, replayable),
                CountsAgainstHost,
                cancellationToken);
        }
        catch (TransientResponseException spent)
        {
            held = null;
            return spent.Response;
        }
        finally
        {
            held?.Dispose();
        }
    }

    internal static bool IsReplayable(HttpRequestMessage request) =>
        request.Method == HttpMethod.Get
        || request.Method == HttpMethod.Head
        || request.Method == HttpMethod.Options
        || request.Method == HttpMethod.Put
        || request.Method == HttpMethod.Delete
        || request.Method == HttpMethod.Trace
        || (request.Options.TryGetValue(Replayable, out var marked) && marked)
        || request.Headers.Contains("Idempotency-Key");

    private static bool IsTransient(HttpStatusCode status) => status is
        HttpStatusCode.RequestTimeout
        or HttpStatusCode.TooManyRequests
        or HttpStatusCode.BadGateway
        or HttpStatusCode.ServiceUnavailable
        or HttpStatusCode.GatewayTimeout;

    private static bool WasNotProcessed(HttpStatusCode status) => status is
        HttpStatusCode.RequestTimeout
        or HttpStatusCode.TooManyRequests
        or HttpStatusCode.ServiceUnavailable;

    private static bool ShouldRetry(Exception ex, bool replayable) => ex switch
    {
        TransientResponseException transient => transient.Retry,
        TimeoutException => replayable,
        HttpRequestException refused when IsBlocked(refused) => false,
        HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError } => true,
        HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } => false,
        HttpRequestException => replayable,
        _ => false,
    };

    private static bool CountsAgainstHost(Exception ex) => ex switch
    {
        TransientResponseException or TimeoutException => true,
        HttpRequestException refused => !IsBlocked(refused),
        _ => false,
    };

    private static bool IsBlocked(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is BlockedAddressException) return true;
        }

        return false;
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header is null) return null;

        var wait = header.Delta ?? (header.Date - DateTimeOffset.UtcNow);
        if (wait is null) return null;

        return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
    }

    /// <summary>
    /// Whether the body survives a second send. A forward-only stream is empty the second time, and
    /// the server answers the empty body without complaint.
    /// </summary>
    private static async Task<bool> CanSendBodyAgainAsync(HttpContent? content)
    {
        if (content is null or ByteArrayContent or ReadOnlyMemoryContent) return true;

        if (content.Headers.ContentLength is not { } length || length > MaxBufferedBody) return false;

        await content.LoadIntoBufferAsync(MaxBufferedBody);
        return true;
    }

    /// <summary>A transient answer, carried out of a try so Carom can decide whether to make another.</summary>
    private sealed class TransientResponseException(HttpResponseMessage response, bool retry)
        : Exception($"Transient answer {(int)response.StatusCode}.")
    {
        public HttpResponseMessage Response { get; } = response;

        public bool Retry { get; } = retry;
    }
}
