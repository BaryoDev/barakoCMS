using System.Threading.RateLimiting;
using barakoCMS.Modules;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.ExternalAuth;

/// <summary>
/// External / social sign-in for barakoCMS. Enable it with:
/// <code>services.AddBarakoCMS(config, m =&gt; m.Add(new ExternalAuthModule()));</code>
///
/// Adds "Continue with Facebook / Google / LinkedIn / GitHub" (<c>GET /api/auth/{provider}/start</c>
/// and <c>/callback</c>). Each provider proves the person's verified email; we match it to a global
/// user (creating one if new) and issue the same tenant-scoped, device-bound token as the built-in
/// flows. Profile details (photo, birthday, location) are captured per provider into a global
/// <see cref="SocialProfile"/> (exposed at <c>GET /api/me/profile</c>). <c>GET /api/auth/providers</c>
/// reports which providers are configured. A provider is active only when its client id/secret are set.
///
/// Any OpenID Connect provider can be added by configuration under <c>Oidc:Providers:{name}</c>
/// (<c>GET /api/auth/oidc/{name}/start</c> and <c>/callback</c>). Those are matched to a user by
/// issuer and subject, kept in <see cref="ExternalIdentity"/>, and by verified email only the first time.
/// A provider that posts its answer back (Apple) gets a POST callback too, and a provider that lists
/// <c>IdTokenAudiences</c> takes <c>POST /api/auth/oidc/{name}/id-token</c> from native apps.
/// </summary>
public sealed class ExternalAuthModule : IBarakoModule
{
    public string Name => "ExternalAuth";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // Outbound HTTP for the OAuth token exchange + userinfo lookups.
        services.AddHttpClient();

        // One per process: they hold the discovery and key cache and the spent states.
        services.AddSingleton<OidcBackchannel>();
        services.AddSingleton<OidcClientSecrets>();
        services.AddSingleton<OidcConsumedStates>();
        services.AddSingleton<OidcUsedNonces>();
        services.AddHostedService<OidcConfigurationReport>();

        // Start and callback are anonymous and each can cost an outbound call, so they get their own
        // bucket per client address instead of only the global one. Read from the root configuration
        // when a partition is created, like the provider sections themselves.
        services.Configure<RateLimiterOptions>(options =>
            options.AddPolicy(OidcSupport.RateLimitPolicy, context =>
            {
                var limits = context.RequestServices.GetRequiredService<IConfiguration>();
                var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

                return RateLimitPartition.GetFixedWindowLimiter($"oidc-{ip}", _ =>
                    new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = OidcSupport.Positive(limits, "PermitLimit", OidcSupport.DefaultPermitLimit),
                        Window = TimeSpan.FromSeconds(
                            OidcSupport.Positive(limits, "WindowSeconds", OidcSupport.DefaultWindowSeconds)),
                    });
            }));
    }

    public void ConfigureSchema(IModuleSchema schema)
    {
        // Profile details belong to the global user identity, not a single tenant.
        schema.For<SocialProfile>()
            .SingleTenanted()
            .DocumentAlias("social_profiles")
            .Index(x => x.UserId, i => i.IsUnique = true);

        // Global for the same reason. The id is derived from issuer and subject, so one provider
        // account has one row, and linking it again replaces that row.
        schema.For<ExternalIdentity>()
            .SingleTenanted()
            .DocumentAlias("external_identities");

        // Global too: a nonce is spent for the issuer, whichever club the sign-in was for.
        schema.For<OidcUsedNonce>()
            .SingleTenanted()
            .DocumentAlias("oidc_used_nonces")
            .Index(x => x.ExpiresAt);
    }
}
