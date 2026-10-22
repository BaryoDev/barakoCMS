using barakoCMS.Infrastructure.Caching;
using barakoCMS.Infrastructure.Multitenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace barakoCMS.Infrastructure.Viewers;

/// <summary>
/// What a module needs to serve a read to a signed-in site viewer the way the core public routes do.
/// </summary>
/// <remarks>
/// The token itself stays internal. A module reads the audience here and projects through
/// <c>IPublicContentProjector</c>, so there is one copy of the token check.
/// </remarks>
public static class ViewerAudience
{
    /// <summary>The request header a viewer's token travels in.</summary>
    public const string Header = ViewerToken.Header;

    /// <summary>
    /// Reads the viewer token the request carries. False when it carries one that does not verify
    /// for <paramref name="tenantSlug"/>: the caller answers 401, never an anonymous read, so an
    /// expired session shows as one rather than as a missing page. <paramref name="member"/> is true
    /// for a verified viewer and false for no header at all.
    /// </summary>
    public static bool TryRead(HttpContext http, IConfiguration config, string tenantSlug, out bool member)
    {
        member = false;
        var raw = http.Request.Headers[ViewerToken.Header].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(raw))
            return true;

        member = ViewerToken.Validate(config, raw, tenantSlug) is not null;
        return member;
    }

    /// <summary>Marks a response built for a viewer: private, no-store, varying on the tenant and the viewer header.</summary>
    public static void SetPrivateCache(HttpContext http) =>
        DeliveryCache.Private(http, TenantResolutionMiddleware.TenantHeader, ViewerToken.Header);
}
