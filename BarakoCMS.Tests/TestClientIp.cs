using System.Net;

namespace BarakoCMS.Tests;

/// <summary>
/// The address a fixture client sends unless its test sends its own.
/// </summary>
/// <remarks>
/// A request to a test host has no remote address, so every client lands in the limiter's one
/// "unknown" bucket and a class waits on the traffic of the classes before it. Each client gets a
/// private address in 100.64.0.0/10 (shared address space, RFC 6598) as a default header named
/// <see cref="TestRemoteIpFilter.DefaultHeader"/>. The range was picked because no test sends a
/// literal address from it as a client address, unlike 198.18.0.0/15 and the documentation ranges.
/// A test that sends <see cref="TestRemoteIpFilter.Header"/> itself, as a default or on the request,
/// wins over it. See #1392.
/// </remarks>
internal static class TestClientIp
{
    private const int RangeSize = 1 << 22;

    private static int _counter;

    /// <summary>An address no earlier client of this process used, until the range of 4194304 wraps.</summary>
    public static string Next()
    {
        var n = (uint)(Interlocked.Increment(ref _counter) - 1) % RangeSize;
        return new IPAddress([100, (byte)(64 + (n >> 16)), (byte)(n >> 8), (byte)n]).ToString();
    }

    public static void Give(HttpClient client) =>
        client.DefaultRequestHeaders.TryAddWithoutValidation(TestRemoteIpFilter.DefaultHeader, Next());
}
