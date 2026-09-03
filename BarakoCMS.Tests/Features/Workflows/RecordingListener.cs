using System.Net;
using System.Text;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// A loopback listener that records whether anything reached it and what body it was sent, and can
/// answer 302 to a second listener.
/// </summary>
/// <remarks>
/// Loopback rather than a real internal address on purpose. Pointing a test at 169.254.169.254
/// either hangs for its timeout or, on a cloud runner, reaches something real. What these tests need
/// is an address the guard blocks, plus a record of whether anything arrived, and loopback is both.
/// </remarks>
internal sealed class RecordingListener : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stopping = new();

    public RecordingListener(string? redirectTo = null)
    {
        Url = $"http://127.0.0.1:{FreePort()}/";
        _listener.Prefixes.Add(Url);
        _listener.Start();
        _ = ServeAsync(redirectTo);
    }

    public string Url { get; }

    public bool WasCalled { get; private set; }

    public string? LastBody { get; private set; }

    /// <summary>The headers of the last request, so a test can assert on what was sent as well as what was in it.</summary>
    public Dictionary<string, string> LastHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);

    private async Task ServeAsync(string? redirectTo)
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

            LastHeaders.Clear();
            foreach (string? name in context.Request.Headers)
            {
                if (name is not null) LastHeaders[name] = context.Request.Headers[name] ?? string.Empty;
            }

            using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
            {
                LastBody = await reader.ReadToEndAsync();
            }

            WasCalled = true;
            if (redirectTo is not null)
            {
                context.Response.StatusCode = 302;
                context.Response.Headers["Location"] = redirectTo;
            }
            else
            {
                context.Response.StatusCode = 200;
            }

            context.Response.Close();
        }
    }

    private static int FreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _listener.Close();
        _stopping.Dispose();
    }
}
