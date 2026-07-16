using Microsoft.AspNetCore.Http;

namespace barakoCMS.Infrastructure.Multitenancy;

/// <summary>
/// Resolves the current tenant from the request host's leading subdomain (the trusted source for
/// tenancy). Apex domains, localhost, and infrastructure subdomains fall back to the default tenant.
/// Records the slug on <see cref="TenantContext"/>; scoping data and auth to it happens downstream.
/// </summary>
public class TenantResolutionMiddleware
{
    private static readonly HashSet<string> InfraSubdomains =
        new(StringComparer.OrdinalIgnoreCase) { "www", "app", "api", "admin" };

    private readonly RequestDelegate _next;

    public TenantResolutionMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, TenantContext tenant)
    {
        var slug = ResolveSlug(context.Request.Host.Host);
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
