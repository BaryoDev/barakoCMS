using FastEndpoints;

namespace BarakoCMS.ExternalAuth;

/// <summary>GET /api/auth/providers — which social sign-in buttons the client should show (those configured).</summary>
public class AuthProvidersEndpoint : EndpointWithoutRequest
{
    private readonly IConfiguration _config;
    public AuthProvidersEndpoint(IConfiguration config) => _config = config;

    public override void Configure()
    {
        Get("/api/auth/providers");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await SendOkAsync(new
        {
            facebook = ExternalAuthSupport.ProviderEnabled(_config, "Facebook", "AppId"),
            google = ExternalAuthSupport.ProviderEnabled(_config, "Google", "ClientId"),
            linkedin = ExternalAuthSupport.ProviderEnabled(_config, "LinkedIn", "ClientId"),
            github = ExternalAuthSupport.ProviderEnabled(_config, "GitHub", "ClientId"),
        }, ct);
    }
}
