using Microsoft.AspNetCore.Http;

namespace barakoCMS.Infrastructure.Multitenancy;

/// <summary>
/// The outcome of resolving a host to a tenant.
/// </summary>
/// <param name="Slug">The tenant, or null to leave the request on the default tenant.</param>
/// <param name="Unrecognised">
/// True when the host looked like it should name a tenant and did not match anything. Callers use
/// this to refuse rather than serve the default tenant's content to an unknown domain.
/// </param>
public readonly record struct TenantResolution(string? Slug, bool Unrecognised);

/// <summary>
/// Resolves the current tenant, in order: the <c>X-Tenant</c> header (path-based routing sets it
/// from the URL handle), then a registered custom domain, then the host's leading subdomain, then
/// the default tenant. For authenticated requests, TenantAccessMiddleware still verifies the token
/// was minted for the resolved tenant, so the header only ever selects a tenant the caller is
/// already authorized for (or public data).
/// </summary>
public class TenantResolutionMiddleware
{
    public const string TenantHeader = "X-Tenant";

    private static readonly HashSet<string> InfraSubdomains =
        new(StringComparer.OrdinalIgnoreCase) { "www", "app", "api", "admin" };

    private readonly RequestDelegate _next;

    public TenantResolutionMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(
        HttpContext context, TenantContext tenant, ITenantDomainSource domains)
    {
        var header = context.Request.Headers[TenantHeader].ToString();

        if (!string.IsNullOrWhiteSpace(header))
        {
            tenant.Slug = header.Trim().ToLowerInvariant();
            await _next(context);
            return;
        }

        var map = await domains.GetAsync(context.RequestAborted);
        var resolved = Resolve(context.Request.Host.Host, map);

        if (resolved.Slug is not null)
            tenant.Slug = resolved.Slug;

        // Serving the default tenant's content to a domain nobody registered is what made the
        // original defect silent. Refusing is opt-in so single-tenant deployments, where every host
        // is legitimately unrecognised, are unaffected.
        if (resolved.Unrecognised && domains.RefuseUnknownHosts)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await _next(context);
    }

    /// <summary>
    /// Resolves a host against the registered domains, then the leading-subdomain rule.
    /// </summary>
    /// <remarks>
    /// A registered domain is checked first so a tenant that owns <c>admin.theirbrand.com</c> can
    /// use it, even though <c>admin</c> is otherwise a reserved infra label.
    /// </remarks>
    public static TenantResolution Resolve(string? host, TenantDomainMap domains)
    {
        var matched = domains.Find(host);
        if (matched is not null)
            return new TenantResolution(matched, false);

        var slug = ResolveSlug(host);
        if (slug is not null)
            return new TenantResolution(slug, false);

        // A host that cannot name a tenant at all (empty, an IP, localhost, an apex we do not know,
        // or a reserved infra label) is only "unrecognised" if it could have been a custom domain.
        return new TenantResolution(null, CouldBeCustomDomain(host));
    }

    /// <summary>The leading subdomain if the host has one and it isn't an infra label; otherwise null.</summary>
    public static string? ResolveSlug(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return null;
        if (System.Net.IPAddress.TryParse(host, out _))
            return null; // an IP address, not a hostname with a subdomain

        var labels = host.Trim().TrimEnd('.').Split('.');
        if (labels.Length < 3) // apex domain, localhost, or an IP
            return null;

        var first = labels[0];
        return InfraSubdomains.Contains(first) ? null : first.ToLowerInvariant();
    }

    /// <summary>
    /// True for a host that names a real domain, so failing to match one is worth reporting.
    /// localhost, IPs and empty hosts are excluded: they are how the app is reached in development
    /// and behind a proxy, and flagging them would make strict mode unusable.
    /// </summary>
    private static bool CouldBeCustomDomain(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;
        if (System.Net.IPAddress.TryParse(host.Trim(), out _))
            return false;

        var value = TenantDomainMap.Normalise(host);
        return value is not null && value.Contains('.');
    }
}
