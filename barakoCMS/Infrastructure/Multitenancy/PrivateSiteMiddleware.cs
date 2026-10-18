using barakoCMS.Features.Site.ShareLinks;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using Marten;
using Microsoft.Net.Http.Headers;

namespace barakoCMS.Infrastructure.Multitenancy;

/// <summary>
/// Answers 404 on every <c>/api/public/*</c> route of a tenant whose <c>site</c> entry has
/// <c>Mode</c> set to <c>Private</c>, unless the caller holds a link to the whole site or is signed
/// in to the tenant with read permission on the <c>site</c> type (#858).
/// </summary>
/// <remarks>
/// One check in front of delivery, so a new public route, in the core or in a module, is covered
/// without touching it. It runs before the output cache, so a cached page is never served to a
/// caller this refused. The link travels in the <c>X-Share-Key</c> header: the frontend redeems it
/// once and sends the key on each delivery request it makes for that visitor. Only a link to the
/// site counts; a link to one entry or page opens that entry through the open route and nothing here.
/// The two share link routes stay reachable, since they are how a visitor gets in. Every delivery
/// request costs one read of the tenant's site entry.
/// </remarks>
internal sealed class PrivateSiteMiddleware(RequestDelegate next)
{
    public const string KeyHeader = "X-Share-Key";
    public const string PrivateMode = "Private";

    private const string DeliveryPrefix = "/api/public";
    private const string ShareLinkPrefix = "/api/public/site/share-links";

    public static bool IsGated(PathString path) =>
        path.StartsWithSegments(DeliveryPrefix, StringComparison.OrdinalIgnoreCase)
        && !path.StartsWithSegments(ShareLinkPrefix, StringComparison.OrdinalIgnoreCase);

    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsGated(context.Request.Path) || HttpMethods.IsOptions(context.Request.Method))
        {
            await next(context);
            return;
        }

        var session = context.RequestServices.GetRequiredService<IQuerySession>();
        var ct = context.RequestAborted;
        var entry = await session.Query<barakoCMS.Models.Content>()
            .Where(c => c.ContentType == ShareLinkKeys.SiteType)
            .OrderBy(c => c.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (entry?.Data is not { } data
            || !data.TryGetValue("Mode", out var mode)
            || !string.Equals(mode?.ToString(), PrivateMode, StringComparison.Ordinal))
        {
            await next(context);
            return;
        }

        if (!await MayViewAsync(context, session, entry, ct))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            context.Response.Headers.CacheControl = "no-store";
            return;
        }

        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderNames.CacheControl] = "private, no-store";
            return Task.CompletedTask;
        });
        await next(context);
    }

    private static async Task<bool> MayViewAsync(
        HttpContext context, IQuerySession session, barakoCMS.Models.Content site, CancellationToken ct)
    {
        var key = context.Request.Headers[KeyHeader].ToString();
        if (key.Length > 0
            && await ShareLinkKeys.FindActiveAsync(session, key, DateTimeOffset.UtcNow, ct) is { EntryId: null, Preview: false })
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
}
