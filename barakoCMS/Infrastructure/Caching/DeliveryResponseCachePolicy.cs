using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Preview;
using barakoCMS.Infrastructure.Viewers;

namespace barakoCMS.Infrastructure.Caching;

/// <summary>
/// Keeps an anonymous delivery read in memory so a repeat does not reach Postgres (#562).
/// </summary>
/// <remarks>
/// <para>
/// The key is the method, host, path and every query key, plus the resolved tenant slug, the
/// X-Tenant header and the Origin header. The tenant slug is in the key itself, so two tenants
/// that ask the same path never share an entry whichever way the tenant was resolved. Origin is in
/// it because the CORS middleware writes Access-Control-Allow-Origin before this runs, and a
/// replayed answer must carry the origin of the caller it is replayed to.
/// </para>
/// <para>
/// A request that carries anything that changes who the caller is, or what they may see, is neither
/// served from the cache nor stored in it: an Authorization header, a viewer token, a share key, a
/// preview token, a cookie. Those reads go to the endpoint exactly as they do with the cache off, so
/// an entry only ever holds what an anonymous caller is shown, and no entry is built for one
/// audience and handed to another.
/// </para>
/// <para>
/// A response is stored only when it is a 200 the endpoint marked as shared in the Short class
/// (<see cref="DeliveryCache.IsShared"/>), and it is tagged with the very tags it sent in
/// Surrogate-Key, tenant first. <see cref="DeliveryResponseCacheListener"/> evicts by those tags
/// when a commit changes what they name.
/// </para>
/// </remarks>
internal sealed class DeliveryResponseCachePolicy : IOutputCachePolicy
{
    public static readonly DeliveryResponseCachePolicy Instance = new();

    private DeliveryResponseCachePolicy() { }

    public ValueTask CacheRequestAsync(OutputCacheContext context, CancellationToken cancellation)
    {
        var http = context.HttpContext;
        var options = http.RequestServices.GetRequiredService<IOptions<DeliveryResponseCacheOptions>>().Value;

        if (!options.Enabled || !IsAnonymousRead(http.Request, options))
        {
            context.EnableOutputCaching = false;
            return ValueTask.CompletedTask;
        }

        var tenant = http.RequestServices.GetRequiredService<TenantContext>().Slug;

        context.EnableOutputCaching = true;
        context.AllowCacheLookup = true;
        context.AllowCacheStorage = true;
        context.AllowLocking = true;
        context.ResponseExpirationTimeSpan = TimeSpan.FromSeconds(Math.Max(1, options.ExpireSeconds));
        context.CacheVaryByRules.QueryKeys = "*";
        context.CacheVaryByRules.HeaderNames =
            new[] { TenantResolutionMiddleware.TenantHeader, HeaderNames.Origin };
        context.CacheVaryByRules.VaryByValues["tenant"] = tenant;
        return ValueTask.CompletedTask;
    }

    public ValueTask ServeFromCacheAsync(OutputCacheContext context, CancellationToken cancellation) =>
        ValueTask.CompletedTask;

    public ValueTask ServeResponseAsync(OutputCacheContext context, CancellationToken cancellation)
    {
        if (!context.EnableOutputCaching || !context.AllowCacheStorage)
            return ValueTask.CompletedTask;

        var response = context.HttpContext.Response;
        var tags = response.Headers[DeliveryCache.SurrogateKeyHeader].ToString();

        if (response.StatusCode != StatusCodes.Status200OK
            || !DeliveryCache.IsShared(response)
            || response.Headers[DeliveryCache.ClassHeader].ToString() != DeliveryCache.Name(DeliveryCacheClass.Short)
            || response.Headers.SetCookie.Count > 0
            || tags.Length == 0)
        {
            context.AllowCacheStorage = false;
            return ValueTask.CompletedTask;
        }

        foreach (var tag in tags.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            context.Tags.Add(tag);

        return ValueTask.CompletedTask;
    }

    private static bool IsAnonymousRead(HttpRequest request, DeliveryResponseCacheOptions options) =>
        (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
        && (request.QueryString.Value?.Length ?? 0) <= options.MaxQueryLength
        && !request.Headers.ContainsKey(HeaderNames.Authorization)
        && !request.Headers.ContainsKey(HeaderNames.Cookie)
        && !request.Headers.ContainsKey(ViewerToken.Header)
        && !request.Headers.ContainsKey(PrivateSite.KeyHeader)
        && !request.Query.ContainsKey(PreviewToken.QueryParam);
}
