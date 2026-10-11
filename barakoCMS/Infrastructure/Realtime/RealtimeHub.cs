using barakoCMS.Core.Interfaces;
using System.IdentityModel.Tokens.Jwt;
using barakoCMS.Infrastructure.Middleware;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace barakoCMS.Infrastructure.Realtime;

/// <summary>
/// Tells signed-in clients that something changed in a topic they hold. It never carries the
/// change itself: a client that hears a notice reads <c>GET /api/changes</c>, where the capability
/// is checked again and the high-water mark applies.
/// </summary>
/// <remarks>
/// A hub method runs in its own service scope, not the HTTP request's, so the tenant the request
/// resolved is not there. It is set from the caller's <c>tenant</c> claim before anything that reads
/// the tenant is resolved.
///
/// The capability, the token's revocation and the user's session epoch are checked when the client
/// subscribes, because TokenValidationMiddleware only sees HTTP requests. The hub is mapped with
/// CloseOnAuthenticationExpiration, so an open connection closes when its access token expires, the
/// same bound HTTP has. Until then a connection whose capability is revoked keeps hearing notices,
/// but a notice holds only a topic, a sequence, a stream id and an event type, and the feed refuses
/// the read that would follow it.
/// </remarks>
[Authorize]
public sealed class RealtimeHub(TopicRegistry topics, TenantContext tenant, IServiceProvider services) : Hub
{
    public const string Path = "/hubs/realtime";

    public static string GroupName(string tenantSlug, string topic) => $"{tenantSlug}:{topic}";

    public async Task Subscribe(string topic)
    {
        var found = topics.Find(topic) ?? throw new HubException($"Unknown topic '{topic}'.");
        var slug = UseCallersTenant();

        var callers = services.GetRequiredService<ICallerCapabilities>();
        if (!await callers.HoldsAsync(Context.User!, found.Capability, Context.ConnectionAborted))
            throw new HubException($"This caller is missing the '{found.Capability}' capability.");

        var user = Context.User!;
        var jti = user.FindFirst(JwtRegisteredClaimNames.Jti)?.Value;
        if (jti is not null
            && await services.GetRequiredService<ITokenRevocationService>().IsTokenRevokedAsync(jti, Context.ConnectionAborted))
            throw new HubException("This token has been revoked.");

        if (await SessionEpochCheck.IsBeforeSessionEpochAsync(
                user,
                services.GetRequiredService<ISessionEpochService>(),
                services.GetRequiredService<ILogger<RealtimeHub>>(),
                Context.ConnectionAborted))
            throw new HubException("This token has been revoked.");

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(slug, found.Name), Context.ConnectionAborted);
    }

    public Task Unsubscribe(string topic) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(UseCallersTenant(), topic), Context.ConnectionAborted);

    private string UseCallersTenant()
    {
        var slug = Context.User?.FindFirst("tenant")?.Value is { Length: > 0 } claimed
            ? claimed.Trim().ToLowerInvariant()
            : Tenant.DefaultSlug;
        tenant.Slug = slug;
        return slug;
    }
}
