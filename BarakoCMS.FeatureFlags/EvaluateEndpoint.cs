using System.Security.Claims;
using FastEndpoints;
using Microsoft.AspNetCore.Http;

namespace BarakoCMS.FeatureFlags;

/// <summary>
/// GET /api/feature-flags — every flag's on/off result for the current request (tenant + user), so
/// the frontend can gate UI. Anonymous-friendly: public pages get flags evaluated with just the club,
/// and see only the flags marked public. An unauthenticated caller is not told the key of anything
/// else, since the name alone reads as a roadmap.
/// </summary>
/// <remarks>
/// Superseded by the OFREP routes under <c>/ofrep/v1/evaluate/flags</c> (#628),
/// which an OpenFeature SDK reads with no client of ours. This route keeps answering through 5.x;
/// every response names its successor in a <c>Link</c> header.
/// </remarks>
public class EvaluateFlagsEndpoint(
    FeatureFlagService flags,
    Marten.IQuerySession session,
    Microsoft.Extensions.Configuration.IConfiguration configuration,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant) : EndpointWithoutRequest<Dictionary<string, bool>>
{
    public override void Configure()
    {
        Get("/api/feature-flags");
        AllowAnonymous();
        Description(b => b.WithDescription(
            "Superseded by POST /ofrep/v1/evaluate/flags (OpenFeature OFREP). Still answers through 5.x."));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var email = User.FindFirst("Username")?.Value ?? User.FindFirst(ClaimTypes.Email)?.Value;
        var ctx = new FlagContext(tenant.Slug, email, email ?? tenant.Slug);
        var audience = await FeatureFlagService.AudienceAsync(session, User, configuration, ct);
        HttpContext.Response.Headers.Link = $"<{Ofrep.OfrepEvaluation.BasePath}>; rel=\"successor-version\"";
        await Send.ResponseAsync(await flags.EvaluateAllAsync(ctx, audience, ct), cancellation: ct);
    }
}
