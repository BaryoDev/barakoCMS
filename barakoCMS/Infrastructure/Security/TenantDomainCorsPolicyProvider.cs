using barakoCMS.Infrastructure.Multitenancy;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

        if (!tenantDomain)
            return policy;

        WithoutCredentialsOnResponse(context);
        return ForTenantDomain(policy, origin);
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
    /// Takes <c>Access-Control-Allow-Credentials</c> off the response to a tenant domain origin, as the
    /// last thing before it is sent.
    /// </summary>
    /// <remarks>
    /// The policy above never sets it. This is for a header already on the response from elsewhere,
    /// such as a cached response replayed with its headers; <c>TenantDomainCorsTests</c> asks that of
    /// the output-cached redirect route. Response-starting callbacks run newest first, and this one is
    /// registered before the CORS middleware registers its own, so it runs after it.
    /// </remarks>
    private static void WithoutCredentialsOnResponse(HttpContext context) =>
        context.Response.OnStarting(() =>
        {
            context.Response.Headers.Remove(HeaderNames.AccessControlAllowCredentials);
            return Task.CompletedTask;
        });

    /// <summary>
    /// With the setting on, adds <c>Vary: Origin</c> to every response that reaches this point,
    /// whether or not the request had an <c>Origin</c>.
    /// </summary>
    /// <remarks>
    /// One URL now answers differently per origin, and the framework adds the header only to an
    /// allowed answer when more than one origin is configured. A public delivery response fetched
    /// without an <c>Origin</c> (by a renderer, say) is cacheable for a minute, and a shared cache
    /// that stored it without this would serve it to a browser on a tenant domain with no allow
    /// header. Registered before the CORS middleware, so it runs after it and sees the header the
    /// middleware may already have added.
    /// </remarks>
    internal static void VaryByOrigin(HttpContext context)
    {
        if (!IsOn(context.RequestServices.GetRequiredService<IConfiguration>()))
            return;

        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            var varies = headers.Vary
                .SelectMany(value => (value ?? string.Empty).Split(','))
                .Any(name => string.Equals(name.Trim(), HeaderNames.Origin, StringComparison.OrdinalIgnoreCase));
            if (!varies)
                headers.Append(HeaderNames.Vary, HeaderNames.Origin);

            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Puts this provider in place of the framework's default one, and leaves a provider the host
    /// registered itself alone.
    /// </summary>
    /// <remarks>
    /// <c>AddCors</c> adds the default only when no provider is registered, so a host that registered
    /// its own before <c>AddBarakoCMS</c> has it here, and replacing it would change that host's CORS
    /// even with the setting off.
    /// </remarks>
    internal static void Register(IServiceCollection services)
    {
        var existing = services.LastOrDefault(d => d.ServiceType == typeof(ICorsPolicyProvider) && !d.IsKeyedService);
        if (existing is not null && existing.ImplementationType != typeof(DefaultCorsPolicyProvider))
            return;

        services.Replace(ServiceDescriptor.Transient<ICorsPolicyProvider, TenantDomainCorsPolicyProvider>());
    }
}
