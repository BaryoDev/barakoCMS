using System.Security.Claims;
using FastEndpoints;

namespace BarakoCMS.FeatureFlags;

/// <summary>
/// GET /api/feature-flags — every flag's on/off result for the current request (tenant + user), so
/// the frontend can gate UI. Anonymous-friendly: public pages get flags evaluated with just the club,
/// and see only the flags marked public. An unauthenticated caller is not told the key of anything
/// else, since the name alone reads as a roadmap.
/// </summary>
public class EvaluateFlagsEndpoint(
    FeatureFlagService flags,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant) : EndpointWithoutRequest<Dictionary<string, bool>>
{
    public override void Configure()
    {
        Get("/api/feature-flags");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var email = User.FindFirst("Username")?.Value ?? User.FindFirst(ClaimTypes.Email)?.Value;
        var ctx = new FlagContext(tenant.Slug, email, email ?? tenant.Slug);
        var audience = User.Identity?.IsAuthenticated == true ? FlagAudience.Authenticated : FlagAudience.Public;
        await Send.ResponseAsync(await flags.EvaluateAllAsync(ctx, audience, ct), cancellation: ct);
    }
}
