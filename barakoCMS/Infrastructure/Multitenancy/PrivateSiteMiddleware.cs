using Marten;
using Microsoft.Net.Http.Headers;

namespace barakoCMS.Infrastructure.Multitenancy;

/// <summary>
/// Answers 404 on every <c>/api/public/*</c> route of a tenant whose <c>site</c> entry has
/// <c>Mode</c> set to <c>Private</c>, unless the caller holds a link to the whole site or is signed
/// in to the tenant with read permission on the <c>site</c> type (#858).
/// </summary>
/// <remarks>
/// One check in front of delivery, so a new public route, in the core or in a module, is covered
/// without touching it. It runs before the output cache, so a cached page is never served to a
/// caller this refused. The link travels in the <c>X-Share-Key</c> header: the frontend redeems it
/// once and sends the key on each delivery request it makes for that visitor. Only a link to the
/// site counts; a link to one entry or page opens that entry through the open route and nothing here.
/// The two share link POST routes stay reachable, since they are how a visitor gets in, and only
/// those two: <c>GET /api/public/site/share-links</c> is the delivery route for a <c>site</c> entry
/// whose slug is <c>share-links</c>. A tenant not in Private mode costs no read per request: that answer is cached per tenant
/// (<see cref="PrivateSiteCache"/>). Otherwise the check is one bounded read (filtered in Postgres, one row) of the tenant's site
/// entries. <c>GET /api/tenants/{handle}/public</c>, which reads another tenant's site entry by
/// name, applies the same rule itself.
/// </remarks>
internal sealed class PrivateSiteMiddleware(RequestDelegate next)
{
    private const string DeliveryPrefix = "/api/public";

    private static readonly string[] ShareLinkPaths =
        ["/api/public/site/share-links/redeem", "/api/public/site/share-links/open"];

    public static bool IsGated(string method, PathString path) =>
        path.StartsWithSegments(DeliveryPrefix, StringComparison.OrdinalIgnoreCase)
        && !HttpMethods.IsOptions(method)
        && !(HttpMethods.IsPost(method) && IsShareLinkRoute(path));

    private static bool IsShareLinkRoute(PathString path) =>
        ShareLinkPaths.Any(p => string.Equals(path.Value?.TrimEnd('/'), p, StringComparison.OrdinalIgnoreCase));

    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsGated(context.Request.Method, context.Request.Path))
        {
            await next(context);
            return;
        }

        var session = context.RequestServices.GetRequiredService<IQuerySession>();
        var ct = context.RequestAborted;

        if (await PrivateSite.FindCachedAsync(context, session, ct) is not { } site)
        {
            await next(context);
            return;
        }

        if (!await PrivateSite.MayViewAsync(context, session, site, ct))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            PrivateSite.Refuse(context);
            return;
        }

        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderNames.CacheControl] = "private, no-store";
            return Task.CompletedTask;
        });
        await next(context);
    }
}
