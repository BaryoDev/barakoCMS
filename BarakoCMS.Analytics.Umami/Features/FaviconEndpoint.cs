using Marten;
using Microsoft.Extensions.Configuration;
using barakoCMS.Infrastructure.Auth;
using FastEndpoints;
using FluentValidation;

namespace BarakoCMS.Analytics.Umami.Features;

public sealed class FaviconRequest
{
    public string Domain { get; set; } = "";
}

public sealed class FaviconRequestValidator : Validator<FaviconRequest>
{
    public FaviconRequestValidator() =>
        RuleFor(x => x.Domain)
            .Must(d => FaviconHostname.TryNormalize(d, out _))
            .WithMessage("domain must be a plain hostname such as example.com.");
}

/// <summary>GET /api/analytics/favicon?domain=example.com, the referrer's favicon, fetched and
/// cached by the server so the console never contacts the referrer's site. 404 when there is none.</summary>
public sealed class FaviconEndpoint(
    FaviconFetcher fetcher,
    IQuerySession session,
    IConfiguration configuration) : Endpoint<FaviconRequest>
{
    public override void Configure()
    {
        Get("/api/analytics/favicon");
        Definition.RequireCapability(
            AnalyticsCapabilities.ViewAnalytics, AnalyticsCapabilities.Defaults.LegacyRoles);
    }

    public override async Task HandleAsync(FaviconRequest req, CancellationToken ct)
    {
        if (!await AnalyticsAccess.AllowedAsync(session, User, configuration, AnalyticsCapabilities.ViewAnalytics, ct))
        {
            ThrowError(PlatformScope.DeploymentWideMessage, 403);
        }

        FaviconHostname.TryNormalize(req.Domain, out var host);
        var icon = await fetcher.GetAsync(host, ct);
        if (icon is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        HttpContext.Response.Headers["X-Content-Type-Options"] = "nosniff";
        HttpContext.Response.Headers.CacheControl = "private, max-age=86400";
        await Send.BytesAsync(icon.Bytes, contentType: icon.ContentType, cancellation: ct);
    }
}
