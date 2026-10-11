using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Multitenancy;
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
/// The capability is checked when the client subscribes. A connection whose capability is revoked
/// later keeps hearing notices until it reconnects, but a notice holds only a topic, a sequence, a
/// stream id and an event type, and the feed refuses the read that would follow it.
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
