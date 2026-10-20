using System.Net;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
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
    /// Starts a Kestrel host listening on a free loopback port the OS picks at bind time.
    /// <c>UseKestrel(port)</c> does nothing on a factory made by <c>WithWebHostBuilder</c>: the derived
    /// factory hands <c>CreateHost</c> to the original, which applies its own port (none), so the host
    /// bound Kestrel's default 127.0.0.1:5000 and collided with other runs. A <c>urls</c> setting is
    /// not honoured either, it reaches configuration but Kestrel never listened on it. The options
    /// callback is applied by the derived factory itself, and an explicit endpoint wins over any URL.
    /// </summary>
    internal static WebApplicationFactory<TEntryPoint> StartOnFreePort<TEntryPoint>(WebApplicationFactory<TEntryPoint> factory)
        where TEntryPoint : class
    {
        var host = factory.WithWebHostBuilder(_ => { });
        host.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        host.StartServer();
        return host;
    }

    /// <summary>
    /// Kestrel records each endpoint in the server's address list after binding it, with the port
    /// the OS assigned, so that list is where the real address is.
    /// </summary>
    internal static HttpClient ExpectContinue<TEntryPoint>(WebApplicationFactory<TEntryPoint> host) where TEntryPoint : class
    {
        var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.SingleOrDefault()
            ?? throw new InvalidOperationException("The host was not started with StartOnFreePort.");
        var handler = new SocketsHttpHandler { UseProxy = false, Expect100ContinueTimeout = ContinueTimeout };
        return new HttpClient(handler) { BaseAddress = new Uri(address) };
    }
}
