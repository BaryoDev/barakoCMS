using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using barakoCMS.Infrastructure.Logging;

namespace barakoCMS.Infrastructure.Multitenancy;

/// <summary>
/// Rejects a request whose token was issued for a different tenant than the one resolved from the
/// host, so a token minted for one tenant can't be replayed against another. Tokens without a tenant
/// claim (legacy) pass through. Runs after authentication, once the user is populated.
///
/// Two kinds of routes are exempt:
/// <list type="bullet">
/// <item>Global identity endpoints under <c>/api/me/</c> operate on global (non-tenant) documents and
/// let a user list and switch between their clubs, so they must be reachable while holding a token
/// scoped to a different club (e.g. from the site root).</item>
/// <item>Public endpoints (routes ending in <c>/public</c>, such as a club's public landing page) are
/// viewable by anyone — including a user authenticated against a different club — because they expose
/// only public information. The tenant-replay guard must not turn these into 403s.</item>
/// </list>
/// </summary>
public class TenantAccessMiddleware(RequestDelegate next, ILogger<TenantAccessMiddleware> logger)
{
    private const string GlobalIdentityPrefix = "/api/me";
    private const string PublicSuffix = "/public";

    public async Task InvokeAsync(HttpContext context, TenantContext tenant)
    {
        var path = context.Request.Path;

        var isGlobalIdentity = path.StartsWithSegments(
            GlobalIdentityPrefix, StringComparison.OrdinalIgnoreCase);

        var isPublic = path.HasValue &&
            path.Value!.EndsWith(PublicSuffix, StringComparison.OrdinalIgnoreCase);

        // In Multi, on every route including the two exempt kinds below: a token minted for the
        // default partition or for a slug with no active tenant, before the switch to Multi or
        // before its tenant was switched off, is not good for the rest of its fifteen minutes. The
        // answer comes from the cached list resolution reads, so this adds no database read.
        if (context.User.Identity is { IsAuthenticated: true }
            && context.User.FindFirst("tenant")?.Value is { Length: > 0 } claimed
            && context.RequestServices.GetRequiredService<TenancyOptions>().IsMulti)
        {
            var slug = claimed.Trim().ToLowerInvariant();
            var active = await context.RequestServices.GetRequiredService<ITenantDomainSource>()
                .GetAsync(context.RequestAborted);

            if (slug == Models.Tenant.DefaultSlug || !active.IsActiveTenant(slug))
            {
                logger.LogWarning(
                    "Tenant access denied: token tenant '{TokenTenant}' is not a registered, active tenant and Tenancy:Mode is Multi, for {Method} {Path}.",
                    LogSafe.Value(claimed), LogSafe.Value(context.Request.Method), LogSafe.Value(path.Value));

                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("This session is not valid for this tenant.");
                return;
            }
        }

        if (!isGlobalIdentity && !isPublic && context.User.Identity is { IsAuthenticated: true })
        {
            var tokenTenant = context.User.FindFirst("tenant")?.Value;
            if (!string.IsNullOrEmpty(tokenTenant) &&
                !string.Equals(tokenTenant, tenant.Slug, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning(
                    "Tenant access denied: token tenant '{TokenTenant}' does not match resolved tenant '{ResolvedTenant}' for {Method} {Path}.",
                    LogSafe.Value(tokenTenant), tenant.Slug, LogSafe.Value(context.Request.Method), LogSafe.Value(path.Value));

                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("This session is not valid for this tenant.");
                return;
            }
        }

        await next(context);
    }
}
