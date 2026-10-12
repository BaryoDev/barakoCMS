namespace barakoCMS.Infrastructure.Http;

/// <summary>
/// The outbound address policy webhooks run under, for a module that calls an address somebody
/// typed into the API.
/// </summary>
/// <remarks>
/// A handler rather than a check on the URL, so the address that was checked is the one dialled
/// and a name that resolves differently the second time cannot reach a private address. It refuses
/// loopback, private, link-local and the other blocked ranges, does not follow redirects and goes
/// through no proxy.
/// </remarks>
public static class OutboundConnections
{
    public static SocketsHttpHandler GuardedHandler() => OutboundHttpHandler.Create(OutboundAddressGuard.Default);
}
