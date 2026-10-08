using Carom;
using Carom.Extensions;
using barakoCMS.Infrastructure.Tracing;

namespace barakoCMS.Infrastructure.Http;

/// <summary>
/// Runs one outbound call with a bounded retry, a timeout on each try and a breaker per tenant and
/// destination, using Carom.
/// </summary>
/// <remarks>
/// Retries run inside the breaker, so one call records one outcome however many tries it took.
///
/// Breakers live in Carom's process-wide store, which holds at most 1,000. Past that it drops the
/// least recently used tenth, and a dropped destination starts again with a closed breaker and no
/// history. Destinations are the hosts workflows are configured to call, so reaching the cap means
/// a thousand distinct hosts in use at once, and the cost of passing it is a breaker that opens a
/// few calls later than it would have. A breaker is per tenant as well, so the count is of
/// tenant and host pairs.
/// </remarks>
internal sealed class OutboundResilience(OutboundResilienceOptions options)
{
    // Carom raises this for every retry in the process. The handler only acts inside a call this
    // class started, so a module's own Carom use is not touched.
    static OutboundResilience() => CaromHooks.OnRetry += BarakoTracing.RecordRetry;

    public static readonly OutboundResilience Default = new(new OutboundResilienceOptions());

    public OutboundResilienceOptions Options => options;

    /// <param name="scope">Separates breakers of different kinds of destination.</param>
    /// <param name="partition">
    /// Whose breaker this is, usually the tenant. A shared host such as a chat or identity provider
    /// is one host to many tenants, and one tenant's failures must not refuse calls for the others.
    /// </param>
    /// <param name="destination">What the breaker is keyed on, and what an open breaker names. A host, never a URL.</param>
    /// <param name="retries">Tries after the first; 0 when the call cannot be sent twice.</param>
    /// <param name="attemptTimeout">How long one try may take.</param>
    /// <param name="attempt">One try, given its number from 1 and a token that fires at <paramref name="attemptTimeout"/>.</param>
    /// <param name="shouldRetry">Whether a failure is worth another try inside this attempt.</param>
    /// <param name="countsAgainstDestination">Whether a failure is the destination's fault, and so counts toward opening its breaker.</param>
    /// <param name="ct">The caller's token. Cancelling it is never a retryable failure.</param>
    /// <exception cref="OutboundCircuitOpenException">The destination's breaker is open, so nothing was sent.</exception>
    /// <exception cref="TimeoutException">The last try ran out of time.</exception>
    public async Task<T> RunAsync<T>(
        string scope,
        string partition,
        string destination,
        int retries,
        TimeSpan attemptTimeout,
        Func<int, CancellationToken, Task<T>> attempt,
        Func<Exception, bool> shouldRetry,
        Func<Exception, bool> countsAgainstDestination,
        CancellationToken ct)
    {
        var bounce = Bounce.Times(retries)
            .WithDelay(options.BaseDelay)
            .WithMaxDelay(options.MaxDelay)
            .When(shouldRetry);

        var number = 0;
        using var call = BarakoTracing.StartOutboundCall(scope, partition, destination);

        // Carom's breaker overloads take an action with no token, so the timeout is applied here
        // rather than with Bounce.WithTimeout, whose token would never reach the send.
        async Task<T> Once()
        {
            var current = ++number;
            using var span = BarakoTracing.StartOutboundTry(current);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(attemptTimeout);
            try
            {
                var result = await attempt(current, timeout.Token);
                BarakoTracing.RecordOutbound(span, BarakoTracing.OutboundOk);
                return result;
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                BarakoTracing.RecordOutbound(span, BarakoTracing.OutboundTimeout);

                // Not an OperationCanceledException, which Carom never retries and which callers
                // read as the caller giving up.
                throw new TimeoutException($"No answer within {attemptTimeout.TotalSeconds:0.#} s.");
            }
            catch
            {
                BarakoTracing.RecordOutbound(span, BarakoTracing.OutboundFailed);
                throw;
            }
        }

        try
        {
            var result = await ShootAsync(Once, bounce, scope, partition, destination, countsAgainstDestination, ct);
            BarakoTracing.RecordOutbound(call, BarakoTracing.OutboundOk, number);
            return result;
        }
        catch (OutboundCircuitOpenException)
        {
            BarakoTracing.RecordOutbound(call, BarakoTracing.OutboundBreakerOpen, number);
            throw;
        }
        catch
        {
            BarakoTracing.RecordOutbound(call, BarakoTracing.OutboundFailed, number);
            throw;
        }
    }

    private async Task<T> ShootAsync<T>(
        Func<Task<T>> once, Bounce bounce, string scope, string partition, string destination,
        Func<Exception, bool> countsAgainstDestination, CancellationToken ct)
    {
        if (options.BreakerFailures == 0)
        {
            return await global::Carom.Carom.ShotAsync(once, bounce, ct);
        }

        var cushion = Cushion.ForService($"barako|{scope}|{options.BreakerFingerprint}|{partition}|{destination}")
            .OpenAfter(options.BreakerFailures, options.BreakerWindow)
            .WithinLast(options.BreakerSampling)
            .When(countsAgainstDestination)
            .HalfOpenAfter(options.BreakerOpen);

        try
        {
            return await CaromCushionExtensions.ShotAsync(once, cushion, bounce, ct);
        }
        catch (CircuitOpenException)
        {
            throw new OutboundCircuitOpenException(destination);
        }
    }
}

/// <summary>
/// A call that was not sent because its destination's breaker is open. The message names the host
/// and nothing else, since it is stored on the run and a URL can carry a credential.
/// </summary>
/// <remarks>
/// An <see cref="HttpRequestException"/>, so every caller that already treats an unreachable host as
/// a retryable failure treats this one the same way.
/// </remarks>
internal sealed class OutboundCircuitOpenException(string destination)
    : HttpRequestException($"Calls to {destination} are paused after repeated failures. The next attempt tries again.")
{
    public string Destination { get; } = destination;
}
