using barakoCMS.Infrastructure.Health;
using barakoCMS.Infrastructure.Security;
using Microsoft.AspNetCore.Http;

namespace barakoCMS.Infrastructure.Multitenancy;

/// <summary>
/// The routes that answer in <see cref="TenancyMode.Multi"/> when the request names no registered,
/// active tenant. Everything else is refused, so a new route is refused until it is added here.
/// </summary>
/// <remarks>
/// A route belongs here only if it reads and writes nothing stored per tenant, because it runs on
/// the default partition:
/// <list type="bullet">
/// <item><c>/health</c> and below, and <c>/metrics</c>: a probe and a scraper name no tenant.</item>
/// <item><c>/api/meta</c>: a console reads the contract version before it knows a tenant.</item>
/// <item><c>/api/tenants/by-host/{host}</c> and <c>/api/tenants/{handle}/public</c>: how a renderer
/// or a sign-in page finds the tenant in the first place. Both read the registry.</item>
/// <item><c>/api/auth</c> and below: identity is stored once for the deployment, and a provider
/// redirects a social sign-in to one fixed address. Which tenant a token is for is the issuer's
/// decision, and in Multi it refuses the default partition.</item>
/// </list>
/// Matching is never looser than routing: a path this refuses that routing would have matched is
/// a 404, and the reverse would be a route served from the default partition.
/// </remarks>
internal static class TenantlessRoutes
{
    public static bool Allows(PathString path)
    {
        var value = path.Value;
        if (string.IsNullOrEmpty(value))
            return false;

        if (HealthProbePaths.IsHealthPath(value) || MetricsScrapeAccess.IsMetricsPath(value))
            return true;

        if (string.Equals(value, "/api/meta", StringComparison.OrdinalIgnoreCase))
            return true;

        if (path.StartsWithSegments("/api/auth", StringComparison.OrdinalIgnoreCase))
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

        return string.Equals(segments[3], "by-host", StringComparison.OrdinalIgnoreCase)
            || string.Equals(segments[4], "public", StringComparison.OrdinalIgnoreCase);
    }
}
