using barakoCMS.Modules;
using Marten;
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
/// </summary>
public sealed class ExternalAuthModule : IBarakoModule
{
    public string Name => "ExternalAuth";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // Outbound HTTP for the OAuth token exchange + userinfo lookups.
        services.AddHttpClient();
    }

    public void ConfigureMarten(StoreOptions options)
    {
        // Profile details belong to the global user identity, not a single tenant.
        options.Schema.For<SocialProfile>()
            .SingleTenanted()
            .DocumentAlias("social_profiles")
            .Index(x => x.UserId, i => i.IsUnique = true);
    }
}
