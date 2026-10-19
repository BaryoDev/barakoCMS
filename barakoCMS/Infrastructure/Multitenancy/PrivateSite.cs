using barakoCMS.Features.Site.ShareLinks;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using Marten;
using Marten.Linq.MatchesSql;
using Content = barakoCMS.Models.Content;

namespace barakoCMS.Infrastructure.Multitenancy;

/// <summary>
/// Whether a tenant's site is in <c>Private</c> mode, and whether a caller may see it anyway (#858).
/// The one place that answers both, for the delivery middleware and for the anonymous tenant lookup.
/// </summary>
/// <remarks>
/// Fails closed: any published <c>site</c> entry with <c>Mode</c> set to <c>Private</c> makes the
/// tenant private, whichever other site entries say otherwise, and the value and the field name are
/// matched without regard to case, as validation and delivery match them.
/// </remarks>
internal static class PrivateSite
{
    public const string KeyHeader = "X-Share-Key";
    public const string PrivateMode = "Private";

    /// <summary>
    /// Any key spelled <c>Mode</c> in any case, holding <c>Private</c> in any case. Matched over every
    /// key of the entry, so <c>{"mode":"Live","Mode":"Private"}</c> is private.
    /// </summary>
    private const string PrivateModeSql =
        "exists (select 1 from jsonb_each_text(d.data -> 'Data') e "
        + "where lower(e.key) = 'mode' and lower(btrim(e.value)) = 'private')";

    /// <summary>
    /// A published site entry in Private mode, or null. One query that filters in Postgres and takes
    /// one row, so a tenant with any number of site entries costs one bounded read, and a Private one
    /// among them is found whatever the rest say (fails closed).
    /// </summary>
    public static Task<Content?> FindPrivateEntryAsync(IQuerySession session, CancellationToken ct) =>
        session.Query<Content>()
            .Where(c => c.ContentType == ShareLinkKeys.SiteType
                        && c.Status == ContentStatus.Published
                        && c.MatchesSql(PrivateModeSql))
            .OrderBy(c => c.CreatedAt)
            .FirstOrDefaultAsync(ct);

    /// <summary>For a request already being served: false once the tenant is private and the caller no longer qualifies.</summary>
    public static async Task<bool> StillAllowedAsync(HttpContext context, CancellationToken ct)
    {
        var session = context.RequestServices.GetRequiredService<IQuerySession>();
        return await FindPrivateEntryAsync(session, ct) is not { } site
               || await MayViewAsync(context, session, site, ct);
    }

    public static async Task<bool> MayViewAsync(
        HttpContext context, IQuerySession session, Content site, CancellationToken ct)
    {
        if (await HoldsSiteLinkAsync(context, session, ct))
        {
            return true;
        }

        if (context.User.Identity is not { IsAuthenticated: true }
            || !Guid.TryParse(context.User.FindFirst("UserId")?.Value, out var userId)
            || await session.LoadAsync<User>(userId, ct) is not { } user)
        {
            return false;
        }

        var permissions = context.RequestServices.GetRequiredService<IPermissionResolver>();
        return await permissions.CanPerformActionAsync(user, ShareLinkKeys.SiteType, "read", site, ct);
    }

    public static async Task<bool> HoldsSiteLinkAsync(HttpContext context, IQuerySession session, CancellationToken ct)
    {
        var key = context.Request.Headers[KeyHeader].ToString();
        return key.Length > 0
            && await ShareLinkKeys.FindActiveAsync(session, key, DateTimeOffset.UtcNow, ct) is { EntryId: null, Preview: false };
    }

    public static void Refuse(HttpContext context) =>
        context.Response.Headers.CacheControl = "no-store";
}
