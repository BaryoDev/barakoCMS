using barakoCMS.Infrastructure.Multitenancy;
using Serilog.Context;

namespace barakoCMS.Infrastructure.Middleware;

/// <summary>
/// Puts the resolved tenant slug on every log event for the rest of the request, as the
/// <c>Tenant</c> property.
/// </summary>
/// <remarks>
/// Runs after tenant resolution. A request that stays on the default tenant carries no property,
/// so a single-tenant deployment's logs do not change.
/// </remarks>
public class TenantLogEnrichmentMiddleware(RequestDelegate next)
{
    public const string PropertyName = "Tenant";

    public async Task InvokeAsync(HttpContext context, TenantContext tenant)
    {
        if (tenant.IsDefault || string.IsNullOrWhiteSpace(tenant.Slug))
        {
            await next(context);
            return;
        }

        using (LogContext.PushProperty(PropertyName, tenant.Slug))
        {
            await next(context);
        }
    }
}
