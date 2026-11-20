using System.Net;

namespace BarakoCMS.Tests;

/// <summary>
/// The address a fixture client sends unless its test sends its own.
/// </summary>
/// <remarks>
/// A request to a test host has no remote address, so every client lands in the limiter's one
/// "unknown" bucket and a class waits on the traffic of the classes before it. Each client gets a
/// private address in 198.18.0.0/15, a range reserved for benchmarking that no test names, as a
/// default header named <see cref="TestRemoteIpFilter.DefaultHeader"/>. A test that sends
/// <see cref="TestRemoteIpFilter.Header"/> itself, as a default or on the request, wins over it. A test
/// that needs the shared bucket on purpose sends <see cref="OptOut"/> in that header. See #1392.
/// </remarks>
internal static class TestClientIp
{
    /// <summary>Not an address, so the filter gives the request none and it lands in the shared bucket.</summary>
    public const string OptOut = "none";

    private const int RangeSize = 1 << 17;

    private static int _counter;

    /// <summary>An address no earlier client of this process used, until the range of 131072 wraps.</summary>
    public static string Next()
    {
        var n = (uint)(Interlocked.Increment(ref _counter) - 1) % RangeSize;
        return new IPAddress([198, (byte)(18 + (n >> 16)), (byte)(n >> 8), (byte)n]).ToString();
    }

    public static void Give(HttpClient client) =>
        client.DefaultRequestHeaders.TryAddWithoutValidation(TestRemoteIpFilter.DefaultHeader, Next());
}
