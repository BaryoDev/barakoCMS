using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;

namespace BarakoCMS.Tests;

/// <summary>
/// Stands in for the Umami instance the analytics module proxies, and records every outbound request
/// it is handed.
/// </summary>
/// <remarks>
/// The module exists so the Umami credentials stay on the server. That is a statement about which
/// bytes leave the process and in which direction, so a test of it has to see both ends: what the
/// endpoint answered the browser, and what the server said to Umami. Letting the real client reach
/// the network would test the internet instead.
///
/// The log is static because the typed client's handler is built by the host, and a test has no
/// other handle on the instance. The analytics tests run in the Sequential collection and each one
/// clears the log before it starts.
/// </remarks>
internal sealed class UmamiStubHandler : HttpMessageHandler
{
    /// <summary>The bearer token the stub hands back from a login. Valid nowhere real.</summary>
    public const string IssuedToken = "umami-session-token-for-tests";

    public sealed record Seen(string Method, string Uri, string? Authorization, string Body);

    private static readonly ConcurrentQueue<Seen> Log = new();

    public static void Clear() => Log.Clear();

    public static IReadOnlyList<Seen> Requests => Log.ToArray();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        var uri = request.RequestUri?.ToString() ?? "";
        Log.Enqueue(new Seen(request.Method.Method, uri, request.Headers.Authorization?.ToString(), body));

        var json = uri switch
        {
            var u when u.EndsWith("api/auth/login", StringComparison.Ordinal)
                => $$"""{"token":"{{IssuedToken}}","user":{"id":"u1","username":"analytics-reader"} }""",
            var u when u.Contains("api/websites?", StringComparison.Ordinal)
                => """{"data":[{"id":"site-1","name":"Playground","domain":"playground.example.com"}],"count":1,"page":1}""",
            var u when u.Contains("/stats", StringComparison.Ordinal)
                => """{"pageviews":120,"visitors":40,"visits":55,"bounces":7,"totaltime":900,"comparison":{"pageviews":100,"visitors":33,"visits":44,"bounces":5,"totaltime":800}}""",
            var u when u.Contains("/active", StringComparison.Ordinal)
                => """{"visitors":3}""",
            var u when u.Contains("/pageviews", StringComparison.Ordinal)
                => """{"pageviews":[{"x":"2026-08-01","y":12}],"sessions":[{"x":"2026-08-01","y":5}]}""",
            var u when u.Contains("/metrics", StringComparison.Ordinal)
                => """[{"x":"/","y":40}]""",
            _ => "{}",
        };

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return response;
    }
}
