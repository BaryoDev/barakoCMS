using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BarakoCMS.Files;

/// <summary>Where clamd is and how long to wait for it.</summary>
public sealed class FileScannerOptions
{
    public const string Section = "Files:Scanner";

    /// <summary>Host and port, as <c>clamav:3310</c>. Empty means no scanner, which is the default.</summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>
    /// How long to give the whole exchange, in seconds.
    /// </summary>
    /// <remarks>
    /// A scan is on the request path, so this is also how long a caller waits before an upload is
    /// refused. Thirty seconds is generous for a ten megabyte file against a local daemon and short
    /// enough that a hung clamd does not hold connections until the web server runs out.
    /// </remarks>
    public int TimeoutSeconds { get; set; } = 30;
}

/// <summary>
/// Talks INSTREAM to a clamd daemon over TCP.
/// </summary>
/// <remarks>
/// The protocol, in full, because it is small and the alternative is a dependency for it: send
/// <c>zINSTREAM\0</c>, then a sequence of chunks each prefixed with its length as a four byte
/// big-endian integer, then a zero length to end the stream. clamd answers one NUL terminated line,
/// <c>stream: OK</c> or <c>stream: Eicar-Signature FOUND</c>, or something ending in <c>ERROR</c>.
///
/// Nothing is cached and no connection is pooled. clamd closes the socket after each INSTREAM, so a
/// pool would hold sockets the daemon has already hung up on.
/// </remarks>
/// <remarks>
/// Public because it is registered by type and because a host assembling its own container needs to
/// be able to construct one, the same reason <see cref="PostgresFileStorage"/> is.
/// </remarks>
public sealed class ClamAvScanner : IFileScanner
{
    /// <summary>
    /// 64 KiB, comfortably under clamd's default StreamMaxLength per chunk.
    /// </summary>
    /// <remarks>
    /// clamd rejects a chunk larger than its own buffer by closing the connection, which surfaces as
    /// a truncated read rather than as a message saying so, so this is picked to stay well inside
    /// every default rather than tuned for throughput.
    /// </remarks>
    private const int ChunkSize = 64 * 1024;

    private readonly string _host;
    private readonly int _port;
    private readonly TimeSpan _timeout;
    private readonly ILogger<ClamAvScanner> _logger;

    public ClamAvScanner(IConfiguration configuration, ILogger<ClamAvScanner> logger)
    {
        _logger = logger;

        var address = configuration[$"{FileScannerOptions.Section}:Address"]?.Trim() ?? string.Empty;
        var seconds = int.TryParse(configuration[$"{FileScannerOptions.Section}:TimeoutSeconds"], out var s) && s > 0
            ? s
            : new FileScannerOptions().TimeoutSeconds;

        _timeout = TimeSpan.FromSeconds(seconds);
        (_host, _port) = Parse(address);
        Configured = _host.Length > 0;
    }

    public bool Configured { get; }

    /// <summary>Splits <c>host:port</c>, defaulting to clamd's own port.</summary>
    private static (string Host, int Port) Parse(string address)
    {
        if (string.IsNullOrWhiteSpace(address)) return (string.Empty, 0);

        var colon = address.LastIndexOf(':');
        if (colon <= 0) return (address, 3310);

        return int.TryParse(address[(colon + 1)..], out var port)
            ? (address[..colon], port)
            : (address, 3310);
    }

    public async Task<ScanResult> ScanAsync(Stream content, CancellationToken ct = default)
    {
        if (!Configured)
        {
            return ScanResult.Unavailable("No scanner is configured.");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_timeout);

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(_host, _port, deadline.Token);

            await using var socket = client.GetStream();

            await socket.WriteAsync(Encoding.ASCII.GetBytes("zINSTREAM\0"), deadline.Token);

            var buffer = new byte[ChunkSize];
            var length = new byte[4];

            int read;
            while ((read = await content.ReadAsync(buffer, deadline.Token)) > 0)
            {
                BinaryPrimitives.WriteInt32BigEndian(length, read);
                await socket.WriteAsync(length, deadline.Token);
                await socket.WriteAsync(buffer.AsMemory(0, read), deadline.Token);
            }

            // Zero length ends the stream. Without it clamd waits for more and the read below blocks
            // until the deadline, which reads as a timeout rather than as a missing terminator.
            BinaryPrimitives.WriteInt32BigEndian(length, 0);
            await socket.WriteAsync(length, deadline.Token);
            await socket.FlushAsync(deadline.Token);

            var answer = await ReadReplyAsync(socket, deadline.Token);

            return Interpret(answer);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller gave up, not the scanner. Rethrowing keeps that distinct from an outage.
            throw;
        }
        catch (Exception ex)
        {
            // Every failure to reach clamd is one verdict: Unavailable. The upload path decides what
            // that means, and it refuses, so being wrong here costs an upload rather than allowing
            // an unscanned one.
            _logger.LogError(ex, "Could not scan an upload with clamd at {Host}:{Port}", _host, _port);
            return ScanResult.Unavailable($"The virus scanner at {_host}:{_port} could not be reached.");
        }
    }

    /// <summary>Reads up to the NUL clamd terminates its reply with.</summary>
    private static async Task<string> ReadReplyAsync(Stream socket, CancellationToken ct)
    {
        var reply = new MemoryStream();
        var buffer = new byte[256];

        int read;
        while ((read = await socket.ReadAsync(buffer, ct)) > 0)
        {
            reply.Write(buffer, 0, read);

            // Bounded, because a daemon that never sends its NUL would otherwise be read until this
            // process runs out of memory. A reply is a short line; anything longer is not one.
            if (Array.IndexOf(buffer, (byte)0, 0, read) >= 0 || reply.Length > 4096) break;
        }

        return Encoding.ASCII.GetString(reply.ToArray()).Trim('\0', '\n', '\r', ' ');
    }

    /// <summary>Turns clamd's one line into a verdict.</summary>
    private static ScanResult Interpret(string reply)
    {
        if (reply.EndsWith("OK", StringComparison.Ordinal))
        {
            return ScanResult.Clean;
        }

        if (reply.EndsWith("FOUND", StringComparison.Ordinal))
        {
            // "stream: Eicar-Signature FOUND" -> "Eicar-Signature".
            var body = reply.StartsWith("stream:", StringComparison.OrdinalIgnoreCase)
                ? reply["stream:".Length..]
                : reply;

            var name = body.Trim();
            name = name.EndsWith("FOUND", StringComparison.Ordinal) ? name[..^"FOUND".Length].Trim() : name;

            return ScanResult.Infected(name.Length > 0 ? name : "unnamed signature");
        }

        // Anything else, including an empty reply and clamd's own ERROR line. Not treated as clean:
        // an unrecognised answer is an answer nobody has verified, which is what Unavailable is for.
        return ScanResult.Unavailable(
            reply.Length > 0 ? $"The virus scanner answered: {reply}" : "The virus scanner answered nothing.");
    }
}
