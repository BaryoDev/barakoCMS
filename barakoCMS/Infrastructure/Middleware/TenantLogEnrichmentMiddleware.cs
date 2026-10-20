using barakoCMS.Infrastructure.Multitenancy;
using Serilog.Context;
using Serilog.Core;
using Serilog.Events;

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
        // Read at log time: API key and station authentication replace the slug after this runs.
        using (LogContext.Push(new TenantEnricher(tenant)))
        {
            await next(context);
        }
    }

    private sealed class TenantEnricher(TenantContext tenant) : ILogEventEnricher
    {
        public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
        {
            if (tenant.IsDefault || string.IsNullOrWhiteSpace(tenant.Slug))
                return;

            logEvent.AddOrUpdateProperty(propertyFactory.CreateProperty(PropertyName, tenant.Slug));
        }
    }
}
