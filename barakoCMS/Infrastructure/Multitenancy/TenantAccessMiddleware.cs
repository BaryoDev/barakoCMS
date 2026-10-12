using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using barakoCMS.Infrastructure.Logging;
using barakoCMS.Models;

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
    private const string DeliveryPrefix = "/api/public";

    public async Task InvokeAsync(HttpContext context, TenantContext tenant)
    {
        var path = context.Request.Path;

        if (tenant.Paused is { } paused)
        {
            await RefusePausedAsync(context, tenant.Slug, paused);
            return;
        }

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

    /// <summary>
    /// Answers a request to a registered tenant that is not Active: 403 to a caller signed in to that
    /// tenant on any route but delivery, 404 to everyone else.
    /// </summary>
    /// <remarks>
    /// Only the tenant's own sessions learn its status. Anyone else gets what a slug nobody
    /// registered gets: in Multi the same 404 and body resolution writes, and in Single a bare 404.
    /// A token for another tenant would have been refused anyway, so it is not told why.
    /// </remarks>
    private async Task RefusePausedAsync(HttpContext context, string slug, TenantStatus paused)
    {
        var claimed = context.User.FindFirst("tenant")?.Value;
        var ownSession = context.User.Identity is { IsAuthenticated: true }
            && (string.IsNullOrEmpty(claimed) || string.Equals(claimed.Trim(), slug, StringComparison.OrdinalIgnoreCase));
        var delivery = context.Request.Path.StartsWithSegments(DeliveryPrefix, StringComparison.OrdinalIgnoreCase);

        logger.LogInformation(
            "Refused {Method} {Path}: tenant '{Tenant}' is {Status}.",
            LogSafe.Value(context.Request.Method), LogSafe.Value(context.Request.Path.Value), slug, paused);

        if (ownSession && !delivery)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync($"This tenant is {paused.ToString().ToLowerInvariant()}.", context.RequestAborted);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status404NotFound;
        if (context.RequestServices.GetRequiredService<TenancyOptions>().IsMulti)
        {
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync(TenantResolutionMiddleware.NoTenantMessage, context.RequestAborted);
        }
    }
}
