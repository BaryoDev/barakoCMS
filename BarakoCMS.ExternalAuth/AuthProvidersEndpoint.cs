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
        bool Has(string key) => !string.IsNullOrWhiteSpace(_config[key]);
        await SendOkAsync(new
        {
            facebook = Has("Facebook:AppId"),
            google = Has("Google:ClientId"),
            linkedin = Has("LinkedIn:ClientId"),
            github = Has("GitHub:ClientId"),
        }, ct);
    }
}
