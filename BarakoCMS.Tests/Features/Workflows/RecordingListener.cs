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

    /// <param name="declaredResponseLength">
    /// When set, the Content-Length announced is this rather than the body's length, and the
    /// connection is held open after the body until the listener is disposed. A client that waits
    /// for the whole body before handing the response back sits here until its own timeout.
    /// </param>
    public RecordingListener(string? redirectTo = null, int statusCode = 200, string? responseBody = null, long? declaredResponseLength = null)
    {
        Url = $"http://127.0.0.1:{FreePort()}/";
        _listener.Prefixes.Add(Url);
        _listener.Start();
        _ = ServeAsync(redirectTo, statusCode, responseBody, declaredResponseLength);
    }

    public string Url { get; }

    public bool WasCalled { get; private set; }

    public string? LastBody { get; private set; }

    /// <summary>The exact bytes received, which is what a signature is computed over.</summary>
    public byte[]? LastBodyBytes { get; private set; }

    /// <summary>The headers of the last request, so a test can assert on what was sent as well as what was in it.</summary>
    public Dictionary<string, string> LastHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);

    private async Task ServeAsync(string? redirectTo, int statusCode, string? responseBody, long? declaredResponseLength)
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

            using (var buffer = new MemoryStream())
            {
                await context.Request.InputStream.CopyToAsync(buffer);
                LastBodyBytes = buffer.ToArray();
                LastBody = Encoding.UTF8.GetString(LastBodyBytes);
            }

            WasCalled = true;
            if (redirectTo is not null)
            {
                context.Response.StatusCode = 302;
                context.Response.Headers["Location"] = redirectTo;
            }
            else
            {
                context.Response.StatusCode = statusCode;
                if (responseBody is not null)
                {
                    var bytes = Encoding.UTF8.GetBytes(responseBody);
                    context.Response.ContentLength64 = declaredResponseLength ?? bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes);
                    await context.Response.OutputStream.FlushAsync();
                }
            }

            if (declaredResponseLength is not null)
            {
                try { await Task.Delay(Timeout.Infinite, _stopping.Token); } catch (OperationCanceledException) { }
                try { context.Response.Abort(); } catch { }
                continue;
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
