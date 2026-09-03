using barakoCMS.Infrastructure.Auth;
using FastEndpoints;
using barakoCMS.Models;
using Marten;

namespace BarakoCMS.Pwa.Features;

public sealed class InstallDto
{
    public Guid? UserId { get; set; }
    public string? Username { get; set; }
    public string? Tenant { get; set; }
    public string? Platform { get; set; }
    public string DisplayMode { get; set; } = "browser";
    public bool Installed { get; set; }
    public string? UserAgent { get; set; }
    public int LaunchCount { get; set; }
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public DateTime? InstalledAt { get; set; }
}

/// <summary>GET /api/pwa/installs — devices that have run the app, newest activity first, with who
/// (when signed in) and whether they're running it installed. Admin only.</summary>
public sealed class InstallsEndpoint : Endpoint<barakoCMS.Models.ListRequest, barakoCMS.Models.PaginatedResponse<InstallDto>>
{
    private readonly IQuerySession _session;

    public InstallsEndpoint(IQuerySession session) => _session = session;

    public override void Configure()
    {
        Get("/api/pwa/installs");
        Definition.RequireCapability(
            PwaCapabilities.ViewPwaInstalls, PwaCapabilities.LegacyRoles);
    }

    public override async Task HandleAsync(barakoCMS.Models.ListRequest req, CancellationToken ct)
    {
        // The Take(1000) cap is gone: the envelope is the bound now, and a cap that silently drops
        // the 1001st row is the kind of quiet wrong answer paging exists to replace.
        var page = await _session.Query<PwaInstall>()
            .OrderByDescending(p => p.LastSeenAt)
            .ToPagedResponseAsync(req, ct);

        var dto = page.Items.Select(p => new InstallDto
        {
            UserId = p.UserId,
            Username = p.Username,
            Tenant = p.Tenant,
            Platform = p.Platform,
            DisplayMode = p.DisplayMode,
            Installed = p.Installed,
            UserAgent = p.UserAgent,
            LaunchCount = p.LaunchCount,
            FirstSeenAt = p.FirstSeenAt,
            LastSeenAt = p.LastSeenAt,
            InstalledAt = p.InstalledAt,
        }).ToList();

        await Send.OkAsync(new barakoCMS.Models.PaginatedResponse<InstallDto>
        {
            Items = dto,
            Page = page.Page,
            PageSize = page.PageSize,
            TotalItems = page.TotalItems,
        }, ct);
    }
}
