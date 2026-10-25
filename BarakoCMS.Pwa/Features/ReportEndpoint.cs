using barakoCMS.Infrastructure.Multitenancy;
using FastEndpoints;
using FluentValidation;
// RequireRateLimiting lives here; this project is not a Web SDK project, so it is not implicitly used.
using Microsoft.AspNetCore.Builder;
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
public sealed class ReportEndpoint(IDocumentSession session, TenantContext tenantContext) : Endpoint<ReportRequest>
{
    public override void Configure()
    {
        Post("/api/pwa/report");
        AllowAnonymous();

        // Unauthenticated ingest, and the caller supplies its own device id, so a loop with fresh
        // ids writes rows without limit. The same shape as the Diagnostics report endpoint, which
        // already takes this budget; this one was left on the global per-IP limit alone.
        Options(x => x.RequireRateLimiting("telemetry"));
    }

    public override async Task HandleAsync(ReportRequest req, CancellationToken ct)
    {
        var userId = Guid.TryParse(User.FindFirst("UserId")?.Value, out var u) ? u : (Guid?)null;
        var username = User.FindFirst("Username")?.Value;
        var tenant = tenantContext.Slug;
        var ua = HttpContext.Request.Headers.UserAgent.ToString();
        var installed = req.Installed
            || req.DisplayMode is "standalone" or "fullscreen" or "minimal-ui";
        var now = DateTime.UtcNow;
        var platform = ReportValidator.NormalizePlatform(req.Platform);

        var existing = await session.Query<PwaInstall>()
            .FirstOrDefaultAsync(p => p.DeviceId == req.DeviceId, ct);

        if (existing is null)
        {
            session.Store(new PwaInstall
            {
                DeviceId = req.DeviceId,
                UserId = userId,
                Username = username,
                Tenant = tenant,
                Platform = platform,
                UserAgent = Trunc(ua),
                DisplayMode = req.DisplayMode ?? "browser",
                Installed = installed,
                InstalledAt = installed ? now : null,
                FirstSeenAt = now,
                LastSeenAt = now,
                LaunchCount = 1,
            });
        }
        else if (existing.Tenant is { Length: > 0 } owner && !owner.Equals(tenant, StringComparison.OrdinalIgnoreCase))
        {
            // The device id belongs to an install of another tenant. A report from here neither
            // updates it nor moves it.
        }
        else
        {
            existing.LastSeenAt = now;
            existing.LaunchCount++;
            if (!string.IsNullOrWhiteSpace(req.DisplayMode)) existing.DisplayMode = req.DisplayMode;
            if (platform is not null) existing.Platform = platform;
            if (!string.IsNullOrWhiteSpace(ua)) existing.UserAgent = Trunc(ua);
            if (userId is not null && (existing.UserId is null || existing.UserId == userId))
            {
                existing.UserId = userId;
                existing.Username = username;
            }

            if (string.IsNullOrWhiteSpace(existing.Tenant)) existing.Tenant = tenant;
            if (installed)
            {
                existing.Installed = true;
                existing.InstalledAt ??= now;
            }
            session.Update(existing);
        }

        await session.SaveChangesAsync(ct);
        await Send.NoContentAsync(ct);
    }

    private static string Trunc(string s) => string.IsNullOrEmpty(s) ? s : (s.Length > 400 ? s[..400] : s);
}

internal sealed class ReportValidator : Validator<ReportRequest>
{
    public const int MaxDeviceIdLength = 100;
    public const int MaxPlatformLength = 40;

    private static readonly string[] DisplayModes = ["browser", "standalone", "fullscreen", "minimal-ui"];

    private static readonly string[] Platforms = ["ios", "android", "windows", "macos", "linux", "other"];

    /// <summary>
    /// One of the documented platforms. A value outside the set is stored as "other" rather than
    /// refused, so a client that sends something new keeps working. Null when none was sent.
    /// </summary>
    internal static string? NormalizePlatform(string? platform)
    {
        if (string.IsNullOrWhiteSpace(platform)) return null;
        var value = platform.Trim().ToLowerInvariant();
        return Platforms.Contains(value) ? value : "other";
    }

    public ReportValidator()
    {
        RuleFor(r => r.DeviceId).NotEmpty().MaximumLength(MaxDeviceIdLength);
        RuleFor(r => r.Platform).MaximumLength(MaxPlatformLength);
        RuleFor(r => r.DisplayMode)
            .Must(m => DisplayModes.Contains(m))
            .When(r => r.DisplayMode is not null)
            .WithMessage("DisplayMode must be one of: browser, standalone, fullscreen, minimal-ui.");
    }
}
