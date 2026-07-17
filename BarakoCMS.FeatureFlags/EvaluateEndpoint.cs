using System.Security.Claims;
using FastEndpoints;

namespace BarakoCMS.FeatureFlags;

/// <summary>
/// GET /api/feature-flags — every flag's on/off result for the current request (tenant + user), so
/// the frontend can gate UI. Anonymous-friendly: public pages get flags evaluated with just the club.
/// </summary>
public class EvaluateFlagsEndpoint : EndpointWithoutRequest<Dictionary<string, bool>>
{
    private readonly FeatureFlagService _flags;
    private readonly barakoCMS.Infrastructure.Multitenancy.TenantContext _tenant;

    public EvaluateFlagsEndpoint(FeatureFlagService flags, barakoCMS.Infrastructure.Multitenancy.TenantContext tenant)
    {
        _flags = flags;
        _tenant = tenant;
    }

    public override void Configure()
    {
        Get("/api/feature-flags");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var email = User.FindFirst("Username")?.Value ?? User.FindFirst(ClaimTypes.Email)?.Value;
        var ctx = new FlagContext(_tenant.Slug, email, email ?? _tenant.Slug);
        await SendAsync(await _flags.EvaluateAllAsync(ctx, ct), cancellation: ct);
    }
}
