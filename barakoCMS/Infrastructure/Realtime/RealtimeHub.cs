using barakoCMS.Core.Interfaces;
using System.IdentityModel.Tokens.Jwt;
using barakoCMS.Infrastructure.Auth.Stations;
using barakoCMS.Infrastructure.Envelope;
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
///
/// A topic a content type declares is held by whoever may read the type. The hub checks that
/// permission when they subscribe and, for a branch scoped type, joins them to the groups of the
/// branches they hold, so a notice reaches only the readers of the entry's branch. A notice names
/// no field value. Rule conditions on the read permission are not run for a notice, only for the
/// feed, which checks each entry before it returns it.
/// </remarks>
[Authorize]
public sealed class RealtimeHub(TopicRegistry topics, TenantContext tenant, IServiceProvider services) : Hub
{
    public const string Path = "/hubs/realtime";

    public static string GroupName(string tenantSlug, string topic) => $"{tenantSlug}:{topic}";

    /// <summary>The group of one branch of a branch scoped content topic.</summary>
    public static string BranchGroupName(string tenantSlug, string topic, Guid branch) => $"{tenantSlug}:{topic}:{branch:N}";

    public override Task OnConnectedAsync()
    {
        if (IsApiKey())
            Context.Abort();
        return base.OnConnectedAsync();
    }

    // API keys stay on the content API. ApiKeyScopeProcessor confines them there, but it only sees
    // FastEndpoints routes. A key has no jti or issue time and its ticket never expires, so a revoked
    // key would also keep a live connection. The hub refuses the principal outright.
    private bool IsApiKey() =>
        string.Equals(Context.User?.FindFirst("auth_method")?.Value, "apikey", StringComparison.Ordinal);

    public async Task Subscribe(string topic)
    {
        Guard();

        var slug = UseCallersTenant();
        if (string.Equals(topic, WorkflowRunTopic.Name, StringComparison.Ordinal))
        {
            if (!WorkflowRunTopic.LegacyRoleAllows(services.GetRequiredService<IConfiguration>(), Context.User!))
                await RequireCapabilityAsync(WorkflowRunTopic.Capability);
            await EnsureSessionValidAsync(Context.ConnectionAborted);
            await JoinAsync(GroupName(slug, WorkflowRunTopic.Name));
            return;
        }

        var module = topics.Find(topic);
        var content = module is null
            ? await services.GetRequiredService<ContentTopicCatalog>().FindAsync(slug, topic, Context.ConnectionAborted)
            : null;
        if (module is null && content is null) throw new HubException($"Unknown topic '{topic}'.");

        if (module is not null) await RequireCapabilityAsync(module);
        else await RequireReadAsync(content!);

        await EnsureSessionValidAsync(Context.ConnectionAborted);

        if (module is not null) await JoinAsync(GroupName(slug, module.Name));
        else await JoinContentGroupsAsync(slug, content!);
    }

    private Task RequireCapabilityAsync(RealtimeTopic found) => RequireCapabilityAsync(found.Capability);

    private async Task RequireCapabilityAsync(string capability)
    {
        var callers = services.GetRequiredService<ICallerCapabilities>();
        if (!await callers.HoldsAsync(Context.User!, capability, Context.ConnectionAborted))
            throw new HubException($"This caller is missing the '{capability}' capability.");
    }

    // The refusals that need no lookup, made before the topic lookup and any group join.
    private void Guard()
    {
        if (IsApiKey())
            throw new HubException("API keys cannot use the realtime hub.");

        // A token that must enroll in MFA hears nothing.
        if (barakoCMS.Infrastructure.Auth.Mfa.MfaEnrollment.MustEnroll(Context.User))
            throw new HubException("Set up MFA before subscribing.");
    }

    // The read permission on the type, the check the content GET starts with. Rule conditions and
    // field values are not decided here: a notice names no values, and the feed checks each entry.
    private async Task RequireReadAsync(ContentTopic content)
    {
        var ct = Context.ConnectionAborted;
        var user = Guid.TryParse(Context.User!.FindFirst("UserId")?.Value, out var id)
            ? await services.GetRequiredService<Marten.IQuerySession>().LoadAsync<User>(id, ct)
            : null;

        if (user is null
            || !await services.GetRequiredService<IPermissionResolver>().CanPerformActionAsync(user, content.ContentType, "read", null, ct))
            throw new HubException($"This caller cannot read '{content.ContentType}'.");
    }

    private async Task EnsureSessionValidAsync(CancellationToken ct)
    {
        var user = Context.User!;

        // A station principal is built once, when the connection opens, so a revoke is read from the
        // store here. A station has no jti and no UserId, so the two checks below pass it by.
        if (await user.IsRevokedStationAsync(services.GetRequiredService<Marten.IDocumentStore>(), ct))
            throw new HubException("This station key has been revoked.");

        var jti = user.FindFirst(JwtRegisteredClaimNames.Jti)?.Value;
        if (jti is not null
            && await services.GetRequiredService<ITokenRevocationService>().IsTokenRevokedAsync(jti, ct))
            throw new HubException("This token has been revoked.");

        if (await SessionEpochCheck.IsBeforeSessionEpochAsync(
                user,
                services.GetRequiredService<ISessionEpochService>(),
                services.GetRequiredService<ILogger<RealtimeHub>>(),
                ct))
            throw new HubException("This token has been revoked.");
    }

    // A topic of a branch scoped type has one group per branch, so a notice reaches only the readers
    // of the entry's branch. The branches are those the caller holds when they subscribe.
    private async Task JoinContentGroupsAsync(string slug, ContentTopic content)
    {
        if (!content.BranchScoped)
        {
            await JoinAsync(GroupName(slug, content.Name));
            return;
        }

        var branches = await services.GetRequiredService<IBranchMembership>()
            .BranchesOfAsync(Context.User!, Context.ConnectionAborted);
        if (branches.Count == 0) throw new HubException("This caller holds no branch.");

        foreach (var branch in branches)
            await JoinAsync(BranchGroupName(slug, content.Name, branch));
    }

    private async Task JoinAsync(string group)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, group, Context.ConnectionAborted);
        Joined().Add(group);
    }

    // Remembered per connection, so Unsubscribe leaves exactly the groups Subscribe joined even when
    // the caller's branches have changed since.
    private HashSet<string> Joined() =>
        (HashSet<string>)(Context.Items[JoinedKey] ??= new HashSet<string>(StringComparer.Ordinal));

    private const string JoinedKey = "joined-groups";

    public async Task Unsubscribe(string topic)
    {
        var plain = GroupName(UseCallersTenant(), topic);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, plain, Context.ConnectionAborted);

        foreach (var group in Joined().Where(g => g.StartsWith(plain + ":", StringComparison.Ordinal)).ToList())
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, group, Context.ConnectionAborted);
            Joined().Remove(group);
        }
    }

    private string UseCallersTenant()
    {
        var slug = Context.User?.FindFirst("tenant")?.Value is { Length: > 0 } claimed
            ? claimed.Trim().ToLowerInvariant()
            : Tenant.DefaultSlug;
        tenant.Slug = slug;
        return slug;
    }
}
