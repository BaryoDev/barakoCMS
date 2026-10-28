using FastEndpoints;
using Marten;

namespace BarakoCMS.ExternalAuth;

/// <summary>
/// GET /api/auth/providers: which social sign-in buttons the client should show (those configured).
/// With <c>?club={handle}</c> it reports only what that tenant allows, and a tenant with no list of
/// its own sees the deployment's providers.
/// </summary>
public class AuthProvidersEndpoint(IConfiguration config, IQuerySession session) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/api/auth/providers");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var club = (Query<string>("club", isRequired: false) ?? "").Trim().ToLowerInvariant();
        var listed = club.Length > OidcSupport.MaxClubLength ? null : await ExternalProviders.ListedAsync(session, club, ct);
        bool Allowed(string key) => listed is null || listed.Contains(key, StringComparer.Ordinal);

        await Send.OkAsync(new
        {
            facebook = Allowed(ExternalProviders.Facebook) && ExternalAuthSupport.ProviderEnabled(config, "Facebook", "AppId"),
            google = Allowed(ExternalProviders.Google) && ExternalAuthSupport.ProviderEnabled(config, "Google", "ClientId"),
            linkedin = Allowed(ExternalProviders.LinkedIn) && ExternalAuthSupport.ProviderEnabled(config, "LinkedIn", "ClientId"),
            github = Allowed(ExternalProviders.GitHub) && ExternalAuthSupport.ProviderEnabled(config, "GitHub", "ClientId"),
            oidc = OidcProviders.Enabled(config)
                .Where(provider => Allowed(ExternalProviders.OidcKey(provider.Name)))
                .Select(provider => new { name = provider.Name, displayName = provider.DisplayName })
                .ToArray(),
        }, ct);
    }
}
