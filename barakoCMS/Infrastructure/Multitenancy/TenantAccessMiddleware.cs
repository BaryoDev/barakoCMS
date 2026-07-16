using Microsoft.AspNetCore.Http;

namespace barakoCMS.Infrastructure.Multitenancy;

/// <summary>
/// Rejects a request whose token was issued for a different tenant than the one resolved from the
/// host, so a token minted for one tenant can't be replayed against another. Tokens without a tenant
/// claim (legacy) pass through. Runs after authentication, once the user is populated.
///
/// Global identity endpoints under <c>/api/me/</c> are exempt: they operate on global (non-tenant)
/// documents and let a user list and switch between their clubs, so they must be reachable while
/// holding a token scoped to a different club (e.g. from the site root).
/// </summary>
public class TenantAccessMiddleware
{
    private const string GlobalIdentityPrefix = "/api/me/";

    private readonly RequestDelegate _next;

    public TenantAccessMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, TenantContext tenant)
    {
        var isGlobalIdentity = context.Request.Path.StartsWithSegments(
            GlobalIdentityPrefix.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

        if (!isGlobalIdentity && context.User.Identity is { IsAuthenticated: true })
        {
            var tokenTenant = context.User.FindFirst("tenant")?.Value;
            if (!string.IsNullOrEmpty(tokenTenant) &&
                !string.Equals(tokenTenant, tenant.Slug, StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("This session is not valid for this tenant.");
                return;
            }
        }

        await _next(context);
    }
}
