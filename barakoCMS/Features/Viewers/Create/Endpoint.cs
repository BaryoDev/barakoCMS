using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Viewers;
using barakoCMS.Models;
using FastEndpoints;

namespace barakoCMS.Features.Viewers.Create;

/// <summary>
/// POST /api/viewer-tokens: a site with its own sign-in mints a token for one of its members.
/// </summary>
/// <remarks>
/// Gated like the other delivery decisions. The token is for the Members audience of public
/// delivery only: it is signed under its own audience, so no authoring route accepts it.
/// </remarks>
[barakoCMS.Infrastructure.Filters.NoIdempotentReplay]
internal sealed class Endpoint(
    IConfiguration config,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant)
    : Endpoint<CreateViewerTokenRequest, CreateViewerTokenResponse>
{
    public override void Configure()
    {
        Post("/api/viewer-tokens");
        Definition.RequireCapability(SystemCapabilities.ManagePublicDelivery, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(CreateViewerTokenRequest req, CancellationToken ct)
    {
        var lifetime = req.LifetimeMinutes is { } m ? TimeSpan.FromMinutes(m) : (TimeSpan?)null;
        var (token, expiresAt) = ViewerToken.Create(config, tenant.Slug, req.Subject.Trim(), lifetime);
        await Send.ResponseAsync(new CreateViewerTokenResponse { Token = token, ExpiresAt = expiresAt }, cancellation: ct);
    }
}
