using FastEndpoints;
using Marten;

namespace BarakoCMS.Pwa.Features;

public sealed class ReportRequest
{
    /// <summary>Stable per-browser id the client persists; dedups repeat launches.</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>standalone | minimal-ui | fullscreen | browser.</summary>
    public string? DisplayMode { get; set; }

    /// <summary>ios | android | windows | macos | linux | other.</summary>
    public string? Platform { get; set; }

    /// <summary>Set true by the client on the <c>appinstalled</c> event (in addition to the display mode).</summary>
    public bool Installed { get; set; }
}

/// <summary>
/// POST /api/pwa/report — the client reports how it's running (installed app vs browser tab), on
/// launch and on install. Anonymous by default; when a signed-in user reports, their identity is
/// recorded so the admin can see who installed the app. Idempotent per device.
/// </summary>
public sealed class ReportEndpoint : Endpoint<ReportRequest>
{
    private readonly IDocumentSession _session;

    public ReportEndpoint(IDocumentSession session) => _session = session;

    public override void Configure()
    {
        Post("/api/pwa/report");
        AllowAnonymous();
    }

    public override async Task HandleAsync(ReportRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.DeviceId))
        {
            await SendResultAsync(Microsoft.AspNetCore.Http.Results.BadRequest("DeviceId is required."));
            return;
        }

        var userId = Guid.TryParse(User.FindFirst("UserId")?.Value, out var u) ? u : (Guid?)null;
        var username = User.FindFirst("Username")?.Value;
        var tenant = HttpContext.Request.Headers["X-Tenant"].FirstOrDefault();
        var ua = HttpContext.Request.Headers.UserAgent.ToString();
        var installed = req.Installed
            || req.DisplayMode is "standalone" or "fullscreen" or "minimal-ui";
        var now = DateTime.UtcNow;

        var existing = await _session.Query<PwaInstall>()
            .FirstOrDefaultAsync(p => p.DeviceId == req.DeviceId, ct);

        if (existing is null)
        {
            _session.Store(new PwaInstall
            {
                DeviceId = req.DeviceId,
                UserId = userId,
                Username = username,
                Tenant = tenant,
                Platform = req.Platform,
                UserAgent = Trunc(ua),
                DisplayMode = req.DisplayMode ?? "browser",
                Installed = installed,
                InstalledAt = installed ? now : null,
                FirstSeenAt = now,
                LastSeenAt = now,
                LaunchCount = 1,
            });
        }
        else
        {
            existing.LastSeenAt = now;
            existing.LaunchCount++;
            if (!string.IsNullOrWhiteSpace(req.DisplayMode)) existing.DisplayMode = req.DisplayMode;
            if (!string.IsNullOrWhiteSpace(req.Platform)) existing.Platform = req.Platform;
            if (!string.IsNullOrWhiteSpace(ua)) existing.UserAgent = Trunc(ua);
            if (userId is not null) { existing.UserId = userId; existing.Username = username; }
            if (!string.IsNullOrWhiteSpace(tenant)) existing.Tenant = tenant;
            if (installed)
            {
                existing.Installed = true;
                existing.InstalledAt ??= now;
            }
            _session.Update(existing);
        }

        await _session.SaveChangesAsync(ct);
        await SendNoContentAsync(ct);
    }

    private static string Trunc(string s) => string.IsNullOrEmpty(s) ? s : (s.Length > 400 ? s[..400] : s);
}
