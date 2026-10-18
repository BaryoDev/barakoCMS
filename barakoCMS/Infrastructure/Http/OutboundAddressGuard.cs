using System.Net;
using System.Net.Sockets;

namespace barakoCMS.Infrastructure.Http;

/// <summary>
/// Decides which addresses an outbound HTTP call may reach, and opens the socket itself so the
/// address that was checked is the address that is dialled.
/// </summary>
/// <remarks>
/// Validating a URL and then handing the name to <c>HttpClient</c> resolves the name twice: once for
/// the check and once for the connection. A name whose answer changes in between passes the check on
/// a public address and connects to a private one (#258). Resolution happens here, exactly once per
/// connection, and the socket is opened to one of the addresses that answer survived, so there is no
/// second lookup to poison.
///
/// The all-or-nothing rule is deliberate. A name answering with one public and one blocked address
/// is refused rather than connected to the public one: a mixed answer is what a rebinding attempt
/// looks like, and picking the survivor would make the attack a retry away.
///
/// The three delegates exist so tests can drive a changing resolver and record which address was
/// dialled without a network. Production uses <see cref="Default"/>, which takes none of them; the
/// registration is asserted by a test that points the real client at loopback.
/// </remarks>
internal sealed class OutboundAddressGuard
{
    public static readonly OutboundAddressGuard Default = new();

    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;
    private readonly Func<IPAddress, bool> _isBlocked;
    private readonly Func<IPAddress, int, CancellationToken, ValueTask<Stream>> _connect;
    private readonly Dictionary<string, HashSet<int>?> _allowed = new(StringComparer.OrdinalIgnoreCase);

    // An allow-list entry with no port reaches only these.
    private static readonly int[] DefaultAllowedPorts = [80, 443];

    public OutboundAddressGuard(
        Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null,
        Func<IPAddress, bool>? isBlocked = null,
        Func<IPAddress, int, CancellationToken, ValueTask<Stream>>? connect = null,
        IReadOnlyCollection<string>? allowedHosts = null)
    {
        foreach (var entry in allowedHosts ?? [])
        {
            var (name, port) = ParseEntry(entry);
            if (name.Length == 0) continue;
            if (!_allowed.TryGetValue(name, out var ports))
                _allowed[name] = ports = [];
            ports!.Add(port ?? 0);
        }
        _resolve = resolve ?? ((host, ct) => Dns.GetHostAddressesAsync(host, ct));
        _isBlocked = isBlocked ?? IsBlockedAddress;
        _connect = connect ?? ConnectSocketAsync;
    }

    /// <summary>
    /// Resolves <paramref name="host"/> once and returns the address to connect to, or throws if the
    /// name does not resolve or any answer is blocked.
    /// </summary>
    public async Task<IPAddress> SelectAddressAsync(string host, CancellationToken ct, int port = 443)
    {
        var addresses = IPAddress.TryParse(host, out var literal)
            ? new[] { literal }
            : await _resolve(host, ct);

        if (addresses.Length == 0)
            throw new HttpRequestException($"Host '{host}' did not resolve to any address.");

        // A listed host relaxes only the ranges an internal receiver lives on. Link-local (the
        // metadata address), unspecified, multicast and reserved stay refused for it.
        var blocked = IsListed(host, port) ? (Func<IPAddress, bool>)IsBlockedEvenWhenListed : _isBlocked;

        foreach (var address in addresses)
        {
            if (blocked(address))
                throw new BlockedAddressException($"Host '{host}' resolves to a blocked address.");
        }

        return addresses[0];
    }

    /// <summary>Whether a host is reachable under this guard. Used for an early, logged refusal.</summary>
    public async Task<bool> IsHostAllowedAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            await SelectAddressAsync(host, ct, port);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Reads <c>Webhooks:AllowedHosts</c>. A value that is not an array (a single string, say) binds
    /// to nothing, so no host is allowed, and a warning says why.
    /// </summary>
    public static string[]? AllowedHostsFrom(Microsoft.Extensions.Configuration.IConfiguration configuration, Microsoft.Extensions.Logging.ILogger logger)
    {
        var section = configuration.GetSection("Webhooks:AllowedHosts");
        var hosts = section.Get<string[]>();
        if (hosts is null && !string.IsNullOrEmpty(section.Value))
            logger.LogWarning(
                "Webhooks:AllowedHosts is set but is not an array, so no host is allowed. Use an array of host names, for example Webhooks:AllowedHosts:0.");
        return hosts;
    }

    private bool IsListed(string host, int port)
    {
        if (!_allowed.TryGetValue(host, out var ports))
            return false;
        // 0 marks an entry written without a port.
        return ports!.Contains(port) || (ports.Contains(0) && DefaultAllowedPorts.Contains(port));
    }

    /// <summary>Splits "host", "host:port" and "[v6]:port". A bare IPv6 literal has no port.</summary>
    private static (string Host, int? Port) ParseEntry(string? entry)
    {
        entry = entry?.Trim() ?? "";
        if (entry.StartsWith('['))
        {
            var close = entry.IndexOf(']');
            if (close < 0) return ("", null);
            var inner = entry[1..close];
            var rest = entry[(close + 1)..];
            if (rest.Length == 0) return (inner, null);
            return rest[0] == ':' && int.TryParse(rest[1..], out var p6) && p6 is > 0 and <= 65535 ? (inner, p6) : ("", null);
        }

        var first = entry.IndexOf(':');
        if (first < 0 || first != entry.LastIndexOf(':'))
            return (entry, null);
        // A malformed port drops the entry, so a typo cannot widen it to the default ports.
        return int.TryParse(entry[(first + 1)..], out var p) && p is > 0 and <= 65535 ? (entry[..first], p) : ("", null);
    }

    public ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
        => ConnectAsync(context.DnsEndPoint, ct);

    public async ValueTask<Stream> ConnectAsync(DnsEndPoint endPoint, CancellationToken ct)
    {
        var address = await SelectAddressAsync(endPoint.Host, ct, endPoint.Port);
        return await _connect(address, endPoint.Port, ct);
    }

    /// <summary>
    /// Loopback, link-local (including the cloud metadata address 169.254.169.254), private,
    /// carrier-grade NAT, multicast and reserved ranges, for IPv4 and IPv6 alike.
    /// </summary>
    /// <remarks>
    /// An IPv4-mapped IPv6 address is unwrapped first. Without that, <c>::ffff:127.0.0.1</c> reaches
    /// loopback while every IPv4 rule below sits unread.
    /// </remarks>
    public static bool IsBlockedAddress(IPAddress ip)
    {
        ip = Unwrap(ip);

        if (IPAddress.IsLoopback(ip))
            return true;
        if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
            return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] == 0) return true;                                  // 0.0.0.0/8
            if (b[0] == 10) return true;                                 // 10.0.0.0/8
            if (b[0] == 127) return true;                                // 127.0.0.0/8
            if (b[0] == 169 && b[1] == 254) return true;                 // 169.254.0.0/16 link-local (cloud metadata)
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;    // 172.16.0.0/12
            if (b[0] == 192 && b[1] == 168) return true;                 // 192.168.0.0/16
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;   // 100.64.0.0/10 CGNAT
            if (b[0] >= 224) return true;                                // 224.0.0.0/4 multicast + reserved
        }
        else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast)
                return true;
            var b = ip.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return true;                      // fc00::/7 unique-local
        }

        return false;
    }

    /// <summary>
    /// Returns the IPv4 address carried by an IPv4-mapped (::ffff:a.b.c.d), IPv4-compatible
    /// (::a.b.c.d) or NAT64 (64:ff9b::a.b.c.d) address, so the IPv4 rules judge it. Anything else is
    /// returned as given.
    /// </summary>
    private static IPAddress Unwrap(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetworkV6)
            return ip;
        if (ip.IsIPv4MappedToIPv6)
            return ip.MapToIPv4();

        var b = ip.GetAddressBytes();
        var zeroPrefix = b.AsSpan(0, 12).IndexOfAnyExcept((byte)0) < 0;
        var nat64Prefix = b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B
            && b.AsSpan(4, 8).IndexOfAnyExcept((byte)0) < 0;
        return zeroPrefix || nat64Prefix ? new IPAddress(b.AsSpan(12, 4)) : ip;
    }

    /// <summary>
    /// The addresses that stay refused for a host on the allow list: unspecified, link-local
    /// (including the cloud metadata address), multicast, reserved and site-local.
    /// </summary>
    public static bool IsBlockedEvenWhenListed(IPAddress ip)
    {
        ip = Unwrap(ip);

        if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
            return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] == 0) return true;                                  // 0.0.0.0/8
            if (b[0] == 169 && b[1] == 254) return true;                 // 169.254.0.0/16 link-local
            if (b[0] >= 224) return true;                                // 224.0.0.0/4 multicast + reserved
        }
        else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast)
                return true;
        }

        return false;
    }

    private static async ValueTask<Stream> ConnectSocketAsync(IPAddress address, int port, CancellationToken ct)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

/// <summary>
/// The guard's refusal of an address. A retry would be refused the same way, so the outbound
/// resilience handler never retries one, and it does not count against the host's breaker.
/// </summary>
internal sealed class BlockedAddressException(string message) : HttpRequestException(message);

/// <summary>
/// The primary handler for outbound calls a workflow can aim: no redirects, and every connection
/// dialled through <see cref="OutboundAddressGuard"/>.
/// </summary>
/// <remarks>
/// Redirects stay off. A redirect is a second resolution reached by another route, so following one
/// puts the target back outside the guard, which is the hole #274 closed.
/// </remarks>
internal static class OutboundHttpHandler
{
    /// <param name="allowProxy">
    /// Whether to honour a system proxy. Off by default, and that is a security decision rather
    /// than a preference.
    ///
    /// With a proxy in use, <c>ConnectCallback</c> dials the proxy, and it is then the proxy that
    /// resolves and connects to the webhook target. The address policy never sees the real
    /// destination, so every guarantee below is void: the guard is inspecting the wrong hop. A
    /// system proxy can arrive from an environment variable that nobody deploying this chose
    /// deliberately, which is the case worth failing closed on.
    ///
    /// An operator whose egress genuinely requires a proxy turns this on with
    /// <c>Webhooks:AllowProxy</c>, and has to apply the same destination policy at the proxy,
    /// because nothing here can.
    /// </param>
    public static SocketsHttpHandler Create(OutboundAddressGuard guard, bool allowProxy = false) => new()
    {
        AllowAutoRedirect = false,
        ConnectCallback = guard.ConnectAsync,
        UseProxy = allowProxy,
    };
}
