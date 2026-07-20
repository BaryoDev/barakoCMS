using FastEndpoints;
using FluentValidation;

namespace BarakoCMS.Analytics.Umami.Features;

public sealed class CreateWebsiteRequest
{
    public string Name { get; set; } = "";
    public string Domain { get; set; } = "";
}

public sealed class CreateWebsiteValidator : Validator<CreateWebsiteRequest>
{
    public CreateWebsiteValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Domain).NotEmpty().MaximumLength(500)
            .Must(d => !d.Contains("://")).WithMessage("Enter a bare domain (e.g. club.baryo.dev), not a full URL.");
    }
}

public sealed class CreateWebsiteResponse
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Domain { get; set; } = "";
    /// <summary>The script tag to paste into the site's &lt;head&gt; so it starts sending data.</summary>
    public string Snippet { get; set; } = "";
}

/// <summary>POST /api/analytics/websites — register a new site in Umami and return its tracking
/// snippet, so an admin can start tracking a site without leaving the CMS.</summary>
public sealed class CreateWebsiteEndpoint : Endpoint<CreateWebsiteRequest, CreateWebsiteResponse>
{
    private readonly IUmamiClient _umami;

    public CreateWebsiteEndpoint(IUmamiClient umami) => _umami = umami;

    public override void Configure()
    {
        Post("/api/analytics/websites");
        Roles("Admin", "SuperAdmin");
    }

    public override async Task HandleAsync(CreateWebsiteRequest req, CancellationToken ct)
    {
        if (!_umami.IsConfigured)
        {
            AddError("Umami is not configured on the server.");
            await SendErrorsAsync(502, ct);
            return;
        }

        var site = await _umami.CreateWebsiteAsync(req.Name.Trim(), req.Domain.Trim(), ct);
        await SendOkAsync(new CreateWebsiteResponse
        {
            Id = site.Id,
            Name = site.Name,
            Domain = site.Domain,
            Snippet = _umami.TrackingSnippet(site.Id),
        }, ct);
    }
}
