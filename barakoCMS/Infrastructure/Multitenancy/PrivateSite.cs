using barakoCMS.Features.Site.ShareLinks;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using Marten;
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

    public static async Task<Content?> FindPrivateEntryAsync(IQuerySession session, CancellationToken ct)
    {
        var entries = await session.Query<Content>()
            .Where(c => c.ContentType == ShareLinkKeys.SiteType && c.Status == ContentStatus.Published)
            .ToListAsync(ct);
        return entries.FirstOrDefault(IsPrivate);
    }

    public static bool IsPrivate(Content entry) =>
        entry.Data is { } data
        && data.FirstOrDefault(p => string.Equals(p.Key, "Mode", StringComparison.OrdinalIgnoreCase)).Value
            is { } mode
        && string.Equals(mode.ToString()?.Trim(), PrivateMode, StringComparison.OrdinalIgnoreCase);

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
