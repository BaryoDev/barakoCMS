using FastEndpoints;
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
public sealed class InstallsEndpoint : EndpointWithoutRequest<List<InstallDto>>
{
    private readonly IQuerySession _session;

    public InstallsEndpoint(IQuerySession session) => _session = session;

    public override void Configure()
    {
        Get("/api/pwa/installs");
        Roles("Admin", "SuperAdmin");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var rows = await _session.Query<PwaInstall>()
            .OrderByDescending(p => p.LastSeenAt)
            .Take(1000)
            .ToListAsync(ct);

        var dto = rows.Select(p => new InstallDto
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

        await SendOkAsync(dto, ct);
    }
}
