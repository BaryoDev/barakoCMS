using Microsoft.AspNetCore.Http;

namespace barakoCMS.Infrastructure.Multitenancy;

/// <summary>
/// Resolves the current tenant, preferring the <c>X-Tenant</c> header (path-based routing sets it
/// from the URL handle) and falling back to the host's leading subdomain. Apex domains, localhost,
/// infra subdomains, and no header fall back to the default tenant. For authenticated requests,
/// TenantAccessMiddleware still verifies the token was minted for the resolved tenant, so the header
/// only ever selects a tenant the caller is already authorized for (or public data).
/// </summary>
public class TenantResolutionMiddleware
{
    public const string TenantHeader = "X-Tenant";

    private static readonly HashSet<string> InfraSubdomains =
        new(StringComparer.OrdinalIgnoreCase) { "www", "app", "api", "admin" };

    private readonly RequestDelegate _next;

    public TenantResolutionMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, TenantContext tenant)
    {
        var header = context.Request.Headers[TenantHeader].ToString();
        var slug = !string.IsNullOrWhiteSpace(header)
            ? header.Trim().ToLowerInvariant()
            : ResolveSlug(context.Request.Host.Host);

        if (!string.IsNullOrEmpty(slug))
            tenant.Slug = slug;

        await _next(context);
    }

    /// <summary>The leading subdomain if the host has one and it isn't an infra label; otherwise null.</summary>
    public static string? ResolveSlug(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return null;
        if (System.Net.IPAddress.TryParse(host, out _))
            return null; // an IP address, not a hostname with a subdomain

        var labels = host.Split('.');
        if (labels.Length < 3) // apex domain, localhost, or an IP
            return null;

        var first = labels[0];
        return InfraSubdomains.Contains(first) ? null : first.ToLowerInvariant();
    }
}
