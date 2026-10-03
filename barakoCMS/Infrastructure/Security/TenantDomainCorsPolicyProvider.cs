using barakoCMS.Infrastructure.Multitenancy;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace barakoCMS.Infrastructure.Security;

/// <summary>
/// The CORS policy, with a tenant's registered domains allowed on top of <c>CORS:AllowedOrigins</c>
/// when <see cref="SettingKey"/> is on.
/// </summary>
/// <remarks>
/// <para>
/// Off by default, so a deployment's allowed origins are exactly the configured list until an
/// operator opts in. On, an origin is allowed when it is <c>https://</c> and a domain an active tenant
/// holds (see <see cref="TenantDomainLookup.OriginHost"/>). Only a platform administrator with
/// <c>manage_tenants</c> can write a tenant's domains, so this widens nothing a tenant administrator
/// controls.
/// </para>
/// <para>
/// A tenant domain origin is never answered with <c>Access-Control-Allow-Credentials</c>. The refresh
/// token lives in a cookie, and a site's pages can run what its editors put on them, so a site must
/// not be able to ride a visitor's API session. Such a page can still call the API with a bearer token
/// it was given, which is what any client can do. An origin that needs credentials is listed in
/// <c>CORS:AllowedOrigins</c>, and a listed origin keeps them whether or not it is also a tenant domain.
/// </para>
/// <para>
/// The domain map is the cached one request routing reads, so a lookup costs no query. A domain change
/// takes effect at once on the instance that wrote it and within <c>Multitenancy:CacheDuration</c>
/// (five minutes by default) on the others. If the map cannot be read, only the configured origins
/// are allowed.
/// </para>
/// </remarks>
internal sealed class TenantDomainCorsPolicyProvider(
    IOptions<CorsOptions> options,
    ITenantDomainSource domains,
    IConfiguration configuration,
    ILogger<TenantDomainCorsPolicyProvider> logger) : ICorsPolicyProvider
{
    public const string PolicyName = "SecurePolicy";

    public const string SettingKey = "CORS:AllowTenantDomains";

    private readonly DefaultCorsPolicyProvider _configured = new(options);

    /// <summary>Whether the setting is on. Anything but a true value, a typo included, leaves it off.</summary>
    internal static bool IsOn(IConfiguration configuration) =>
        bool.TryParse(configuration[SettingKey], out var on) && on;

    public async Task<CorsPolicy?> GetPolicyAsync(HttpContext context, string? policyName)
    {
        var policy = await _configured.GetPolicyAsync(context, policyName);
        if (policy is null || !string.Equals(policyName, PolicyName, StringComparison.Ordinal) || !IsOn(configuration))
            return policy;

        var origin = context.Request.Headers.Origin.ToString();
        var tenantDomain = !policy.IsOriginAllowed(origin) && await IsTenantDomainAsync(origin, context.RequestAborted);

        OnResponseStarting(context, tenantDomain);
        return tenantDomain ? ForTenantDomain(policy, origin) : policy;
    }

    private async Task<bool> IsTenantDomainAsync(string origin, CancellationToken ct)
    {
        var host = TenantDomainLookup.OriginHost(origin);
        if (host is null)
            return false;

        try
        {
            return (await domains.GetAsync(ct)).Find(host) is not null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The tenant domain map could not be read, so only CORS:AllowedOrigins is allowed for this request");
            return false;
        }
    }

    private static CorsPolicy ForTenantDomain(CorsPolicy configured, string origin)
    {
        var policy = new CorsPolicy
        {
            SupportsCredentials = false,
            PreflightMaxAge = configured.PreflightMaxAge,
        };
        policy.Origins.Add(origin);
        policy.Headers.Add("*");
        policy.Methods.Add("*");
        foreach (var header in configured.ExposedHeaders)
            policy.ExposedHeaders.Add(header);

        return policy;
    }

    /// <summary>
    /// Runs after the CORS middleware has written its headers, since response-starting callbacks run
    /// newest first and this one is registered before the middleware registers its own.
    /// </summary>
    /// <remarks>
    /// <c>Vary: Origin</c> on every answer, allowed or not: with tenant domains on, one URL answers
    /// differently per origin, and the framework adds it only to an allowed answer when more than
    /// one origin is configured. A shared cache that ignored that would hand one origin's answer to
    /// another.
    ///
    /// <c>Access-Control-Allow-Credentials</c> is removed for a tenant domain even though its policy
    /// never sets it, because the output cache replays the headers it stored: a response cached for
    /// a listed origin carries the header, and the CORS middleware only ever adds it, never clears it.
    /// </remarks>
    private static void OnResponseStarting(HttpContext context, bool tenantDomain)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            if (tenantDomain)
                headers.Remove(HeaderNames.AccessControlAllowCredentials);

            var varies = headers.Vary
                .SelectMany(value => (value ?? string.Empty).Split(','))
                .Any(name => string.Equals(name.Trim(), HeaderNames.Origin, StringComparison.OrdinalIgnoreCase));
            if (!varies)
                headers.Append(HeaderNames.Vary, HeaderNames.Origin);

            return Task.CompletedTask;
        });
    }
}
