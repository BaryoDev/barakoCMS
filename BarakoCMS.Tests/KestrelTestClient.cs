using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests;

/// <summary>
/// A client for a test host started with <c>UseKestrel</c>, for a request that sends
/// <c>Expect: 100-continue</c> so Kestrel can refuse an oversized body on its Content-Length.
/// </summary>
/// <remarks>
/// The default handler waits one second for the server's answer and then sends the body anyway. On a
/// slow runner Kestrel can then refuse and close while the client is still writing, and the test sees
/// a broken pipe instead of the status (#1143). This one waits long enough that the body is never
/// sent before the answer arrives.
/// </remarks>
internal static class KestrelTestClient
{
    private static readonly TimeSpan ContinueTimeout = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Starts a Kestrel host on a port picked here and set as the host's URL. <c>UseKestrel(0)</c>
    /// alone does nothing for a minimal-hosting entry point (the factory only applies the port to a
    /// web host it builds itself), so the host bound Kestrel's default, 127.0.0.1:5000, and collided
    /// with another run on the same machine. A URL with port 0 binds a free port but leaves the
    /// server's address list without it, so the port is chosen first and read back from the setting.
    /// </summary>
    internal static WebApplicationFactory<TEntryPoint> StartOnFreePort<TEntryPoint>(WebApplicationFactory<TEntryPoint> factory)
        where TEntryPoint : class
    {
        var port = FreePort();
        var host = factory.WithWebHostBuilder(builder => builder.UseUrls($"http://127.0.0.1:{port}"));
        host.UseKestrel(port);
        host.StartServer();
        return host;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    internal static HttpClient ExpectContinue<TEntryPoint>(WebApplicationFactory<TEntryPoint> host) where TEntryPoint : class
    {
        var address = host.Services.GetRequiredService<IConfiguration>()[WebHostDefaults.ServerUrlsKey]
            ?? throw new InvalidOperationException("The host was not started with StartOnFreePort.");
        var handler = new SocketsHttpHandler { UseProxy = false, Expect100ContinueTimeout = ContinueTimeout };
        return new HttpClient(handler) { BaseAddress = new Uri(address) };
    }
}
