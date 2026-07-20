using FastEndpoints;

namespace BarakoCMS.Analytics.Umami.Features;

public sealed class WebsiteDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Domain { get; set; } = "";
}

public sealed class WebsitesResponse
{
    /// <summary>False when the module is present but Umami isn't configured — the admin shows a
    /// "connect Umami" hint instead of an error.</summary>
    public bool Configured { get; set; }
    public List<WebsiteDto> Websites { get; set; } = new();
}

/// <summary>GET /api/analytics/websites — the sites Umami is tracking, for the admin's picker.</summary>
public sealed class WebsitesEndpoint : EndpointWithoutRequest<WebsitesResponse>
{
    private readonly IUmamiClient _umami;

    public WebsitesEndpoint(IUmamiClient umami) => _umami = umami;

    public override void Configure()
    {
        Get("/api/analytics/websites");
        Roles("Admin", "SuperAdmin");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!_umami.IsConfigured)
        {
            await SendOkAsync(new WebsitesResponse { Configured = false }, ct);
            return;
        }

        var sites = await _umami.GetWebsitesAsync(ct);
        await SendOkAsync(new WebsitesResponse
        {
            Configured = true,
            Websites = sites.Select(w => new WebsiteDto { Id = w.Id, Name = w.Name, Domain = w.Domain }).ToList(),
        }, ct);
    }
}
