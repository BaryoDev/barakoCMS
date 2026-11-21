using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BarakoCMS.Tests;

/// <summary>One purge request the stand-in received.</summary>
internal sealed record PurgeCall(string Zone, IReadOnlyList<string> Tags, string? Authorization);

/// <summary>
/// A loopback stand-in for the CDN provider's purge API (Cloudflare shape), for the purge-on-commit
/// tests (#561). It records every call by zone, and a test can make a zone answer a status, hold the
/// answer until released, or never answer.
/// </summary>
/// <remarks>
/// One per test run, because the host's outbound allow list names its port. A test uses its own zone
/// id, so what it reads back is its own and tests do not see each other's calls.
/// </remarks>
internal sealed class CdnPurgeStandIn : IDisposable
{
    private static readonly Regex Path = new(@"^/client/v4/zones/(?<zone>[A-Za-z0-9]+)/purge_cache$", RegexOptions.Compiled);

    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentQueue<PurgeCall> _calls = new();
    private readonly ConcurrentDictionary<string, int> _status = new();
    private readonly ConcurrentDictionary<string, bool> _echoToken = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _hold = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _arrived = new();
    private readonly ConcurrentDictionary<string, bool> _hang = new();

    public static CdnPurgeStandIn Shared { get; } = new();

    private CdnPurgeStandIn()
    {
        var port = FreePort();
        Port = port;
        BaseUrl = $"http://127.0.0.1:{port}/client/v4";
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    public int Port { get; }

    /// <summary>What the host's <c>Delivery:CdnPurge:BaseUrl:Cloudflare</c> is set to.</summary>
    public string BaseUrl { get; }

    public IReadOnlyList<PurgeCall> CallsFor(string zone) =>
        _calls.Where(c => c.Zone == zone).ToList();

    public void AnswerWith(string zone, int status, bool echoAuthorization = false)
    {
        _status[zone] = status;
        _echoToken[zone] = echoAuthorization;
    }

    /// <summary>The zone never answers, until the stand-in is disposed.</summary>
    public void Hang(string zone) => _hang[zone] = true;

    /// <summary>The zone records the call, then waits for the release action before it answers.</summary>
    public (Task Arrived, Action Release) HoldNextCall(string zone)
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _arrived[zone] = arrived;
        _hold[zone] = release;
        return (arrived.Task, () => release.TrySetResult());
    }

    private async Task ServeAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            var match = Path.Match(context.Request.Url?.AbsolutePath ?? string.Empty);
            if (!match.Success || context.Request.HttpMethod != "POST")
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
                return;
            }

            var zone = match.Groups["zone"].Value;
            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            using var json = JsonDocument.Parse(body);
            var tags = json.RootElement.GetProperty("tags").EnumerateArray().Select(t => t.GetString()!).ToList();
            var auth = context.Request.Headers["Authorization"];
            _calls.Enqueue(new PurgeCall(zone, tags, auth));

            if (_arrived.TryRemove(zone, out var arrived))
            {
                arrived.TrySetResult();
            }

            if (_hold.TryRemove(zone, out var release))
            {
                await Task.WhenAny(release.Task, Task.Delay(Timeout.Infinite, _stopping.Token));
            }

            if (_hang.ContainsKey(zone))
            {
                await Task.Delay(Timeout.Infinite, _stopping.Token);
            }

            var status = _status.TryGetValue(zone, out var s) ? s : 200;
            var echo = _echoToken.TryGetValue(zone, out var e) && e ? $" received {auth}" : string.Empty;
            var payload = status is >= 200 and < 300
                ? """{"success":true,"errors":[]}"""
                : $$"""{"success":false,"errors":[{"code":1000,"message":"stand-in refused{{echo}}"}]}""";
            var bytes = Encoding.UTF8.GetBytes(payload);
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
        catch
        {
            try
            {
                context.Response.Abort();
            }
            catch
            {
                // The listener is going away.
            }
        }
    }

    private static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _listener.Close();
    }
}
