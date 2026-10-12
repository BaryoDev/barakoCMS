using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace BarakoCMS.Diagnostics.Forwarding;

/// <summary>What a report carried, copied at ingest so the sender needs nothing from the request.</summary>
internal sealed record ForwardedError(
    string Fingerprint,
    string Kind,
    string Severity,
    string Message,
    string? Source,
    int? Status,
    string? Url,
    string? AppVersion,
    string? Tenant,
    DateTimeOffset At);

/// <summary>
/// The queue between ingest and the receiver. Ingest only ever calls <see cref="TryEnqueue"/>, which
/// never waits and never throws, so a receiver that is down, slow or wrong cannot touch the
/// response to <c>POST /api/client-errors</c>.
/// </summary>
internal sealed class ClientErrorForwarder
{
    private readonly Channel<ForwardedError>? _queue;
    private long _dropped;

    public ClientErrorForwarder(IOptions<ForwardingOptions> options)
    {
        var o = options.Value;
        Dsn = o.ParsedDsn;
        if (Dsn is null)
        {
            return;
        }

        _queue = Channel.CreateBounded<ForwardedError>(new BoundedChannelOptions(Math.Max(1, o.QueueCapacity))
        {
            // Wait, so TryWrite answers false when the queue is full instead of quietly evicting.
            // Nothing ever waits on it: ingest only calls TryWrite.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });
    }

    internal SentryDsn? Dsn { get; }

    public bool Enabled => _queue is not null;

    /// <summary>Reports dropped because the queue was full.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    internal ChannelReader<ForwardedError>? Reader => _queue?.Reader;

    public bool TryEnqueue(ForwardedError error)
    {
        if (_queue is null)
        {
            return false;
        }
        if (_queue.Writer.TryWrite(error))
        {
            return true;
        }
        Interlocked.Increment(ref _dropped);
        return false;
    }
}
