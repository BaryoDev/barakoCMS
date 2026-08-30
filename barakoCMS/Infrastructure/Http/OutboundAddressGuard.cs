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

    public OutboundAddressGuard(
        Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null,
        Func<IPAddress, bool>? isBlocked = null,
        Func<IPAddress, int, CancellationToken, ValueTask<Stream>>? connect = null)
    {
        _resolve = resolve ?? ((host, ct) => Dns.GetHostAddressesAsync(host, ct));
        _isBlocked = isBlocked ?? IsBlockedAddress;
        _connect = connect ?? ConnectSocketAsync;
    }

    /// <summary>
    /// Resolves <paramref name="host"/> once and returns the address to connect to, or throws if the
    /// name does not resolve or any answer is blocked.
    /// </summary>
    public async Task<IPAddress> SelectAddressAsync(string host, CancellationToken ct)
    {
        var addresses = IPAddress.TryParse(host, out var literal)
            ? new[] { literal }
            : await _resolve(host, ct);

        if (addresses.Length == 0)
            throw new HttpRequestException($"Host '{host}' did not resolve to any address.");

        foreach (var address in addresses)
        {
            if (_isBlocked(address))
                throw new HttpRequestException($"Host '{host}' resolves to a blocked address.");
        }

        return addresses[0];
    }

    /// <summary>Whether a host is reachable under this guard. Used for an early, logged refusal.</summary>
    public async Task<bool> IsHostAllowedAsync(string host, CancellationToken ct)
    {
        try
        {
            await SelectAddressAsync(host, ct);
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

    public ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
        => ConnectAsync(context.DnsEndPoint, ct);

    public async ValueTask<Stream> ConnectAsync(DnsEndPoint endPoint, CancellationToken ct)
    {
        var address = await SelectAddressAsync(endPoint.Host, ct);
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
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

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
