using barakoCMS.Infrastructure.Health;
using barakoCMS.Infrastructure.Security;
using Microsoft.AspNetCore.Http;

namespace barakoCMS.Infrastructure.Multitenancy;

/// <summary>
/// The routes that answer in <see cref="TenancyMode.Multi"/> when the request names no registered,
/// active tenant. Everything else is refused, so a new route is refused until it is added here.
/// </summary>
/// <remarks>
/// A route belongs here only if it reads nothing stored per tenant through the request's own
/// session, because that session runs on the default partition:
/// <list type="bullet">
/// <item><c>/health</c> and below, and <c>/metrics</c>: a probe and a scraper name no tenant.</item>
/// <item><c>/api/meta</c>: a console reads the contract version before it knows a tenant.</item>
/// <item><c>/api/tenants/by-host/{host}</c> and <c>/api/tenants/{handle}/public</c>: how a renderer
/// or a sign-in page finds the tenant in the first place. Both read the registry. The second then
/// reads the published site entry of the tenant the registry returned, in a session opened for
/// that tenant by name, so it reaches a registered, active tenant's partition and no other.</item>
/// <item><c>/api/tenants/tls-ask</c>: a reverse proxy asks whether a host is a tenant's domain, on
/// the deployment's own host. It reads the registry.</item>
/// <item><c>/api/auth</c> and below: identity is stored once for the deployment, and a provider
/// redirects a social sign-in to one fixed address. Which tenant a token is for is the issuer's
/// decision, and in Multi it refuses the default partition.</item>
/// </list>
/// The one thing these write that carries a tenant is the audit log, which is a single table with
/// the tenant kept as data. A sign-in let through here is recorded under <c>default</c>, whatever
/// slug the caller sent, so a tenant's own trail does not hold it.
///
/// Each route is allowed for the methods it serves and no others, and a handle that is one of the
/// literal segments under <c>/api/tenants</c> is not a handle. Routing would otherwise hand
/// <c>/api/tenants/members/public</c> to <c>PUT</c> and <c>DELETE /api/tenants/members/{userId}</c>.
/// Matching is never looser than routing: a path this refuses that routing would have matched is
/// a 404, and the reverse would be a route served from the default partition.
/// </remarks>
internal static class TenantlessRoutes
{
    /// <summary>Segments that follow <c>/api/tenants/</c> as part of a route, so none is a tenant's handle.</summary>
    private static readonly HashSet<string> RouteLiterals =
        new(StringComparer.OrdinalIgnoreCase) { "by-host", "members" };

    public static bool Allows(string method, PathString path)
    {
        var value = path.Value;
        if (string.IsNullOrEmpty(value))
            return false;

        if (path.StartsWithSegments("/api/auth", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method))
            return false;

        if (HealthProbePaths.IsHealthPath(value) || MetricsScrapeAccess.IsMetricsPath(value))
            return true;

        if (string.Equals(value, "/api/meta", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "/api/tenants/tls-ask", StringComparison.OrdinalIgnoreCase))
            return true;

        // ["", "api", "tenants", x, y]
        var segments = value.Split('/');
        if (segments.Length != 5
            || !string.Equals(segments[1], "api", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(segments[2], "tenants", StringComparison.OrdinalIgnoreCase)
            || segments[3].Length == 0
            || segments[4].Length == 0)
        {
            return false;
        }

        if (string.Equals(segments[3], "by-host", StringComparison.OrdinalIgnoreCase))
            return true;

        return !RouteLiterals.Contains(segments[3])
            && string.Equals(segments[4], "public", StringComparison.OrdinalIgnoreCase);
    }
}
