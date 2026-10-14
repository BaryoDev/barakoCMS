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

    internal static HttpClient ExpectContinue<TEntryPoint>(WebApplicationFactory<TEntryPoint> host) where TEntryPoint : class
    {
        var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var handler = new SocketsHttpHandler { UseProxy = false, Expect100ContinueTimeout = ContinueTimeout };
        return new HttpClient(handler) { BaseAddress = new Uri(address) };
    }
}
