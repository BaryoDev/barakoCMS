using FastEndpoints;

namespace BarakoCMS.ExternalAuth;

/// <summary>GET /api/auth/providers — which social sign-in buttons the client should show (those configured).</summary>
public class AuthProvidersEndpoint(IConfiguration config) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/api/auth/providers");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await Send.OkAsync(new
        {
            facebook = ExternalAuthSupport.ProviderEnabled(config, "Facebook", "AppId"),
            google = ExternalAuthSupport.ProviderEnabled(config, "Google", "ClientId"),
            linkedin = ExternalAuthSupport.ProviderEnabled(config, "LinkedIn", "ClientId"),
            github = ExternalAuthSupport.ProviderEnabled(config, "GitHub", "ClientId"),
        }, ct);
    }
}
