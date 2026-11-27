using barakoCMS.Features.Queries.Run;
using barakoCMS.Features.Site.ShareLinks;
using barakoCMS.Infrastructure.Logging;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using Marten;
using Microsoft.Extensions.Logging;
using Serilog.Extensions.Logging;

namespace barakoCMS.Infrastructure.Rollups;

/// <summary>A local day and the zone it is a day in.</summary>
internal sealed record RollupToday(DateOnly Date, TimeZoneInfo Zone);

/// <summary>
/// The time a rollup run counts by. The day is read once, on first use, so a run that crosses
/// midnight counts every header by one day, and the zone is read from the site setting then too.
/// </summary>
/// <remarks>
/// The site <c>TimeZone</c> is the zone, as the rule engine reads it. No site, no setting, or a zone
/// this server does not know is UTC, and an unknown one logs one warning for the run and never fails it.
/// </remarks>
internal sealed class RollupClock(TimeProvider? time = null, ILogger? log = null)
{
    private static readonly ILogger Fallback = new SerilogLoggerFactory().CreateLogger(typeof(RollupClock).FullName!);

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    private readonly ILogger _log = log ?? Fallback;

    private RollupToday? _today;

    public DateTimeOffset Now => _time.GetUtcNow();

    /// <summary>The local day and zone of the session's tenant, read once for this clock.</summary>
    public async Task<RollupToday> TodayAsync(IQuerySession session, CancellationToken ct)
    {
        if (_today is not null)
        {
            return _today;
        }

        var site = await session.Query<Content>()
            .Where(c => c.ContentType == ShareLinkKeys.SiteType && c.Status == ContentStatus.Published)
            .OrderBy(c => c.CreatedAt)
            .Take(1)
            .FirstOrDefaultAsync(ct);
        var named = EntryUrls.Setting(site, "TimeZone");
        var zone = TemplateExpression.Zone(named);
        if (named is not null && zone is null)
        {
            _log.LogWarning(
                "Tenant {Tenant} has a site time zone this server does not know, so its rollup windows count in UTC: {Zone}",
                LogSafe.Value(session.TenantId), LogSafe.Value(named));
        }

        zone ??= TimeZoneInfo.Utc;
        _today = new RollupToday(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(Now, zone).DateTime), zone);
        return _today;
    }
}
