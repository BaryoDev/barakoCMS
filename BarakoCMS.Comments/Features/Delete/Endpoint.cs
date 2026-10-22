using Microsoft.Extensions.Configuration;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Services;
using FastEndpoints;
using Marten;

namespace BarakoCMS.Comments.Features.Delete;

internal sealed class Request
{
    public Guid Id { get; set; }
}

/// <summary>
/// DELETE /api/comments/{id}: removes a comment and every reply under it. Either capability gets a
/// caller to the handler. The author may delete a subtree that is all their own, and a caller holding
/// manage_comments may delete any, and both only while they can still read the record. A moderator
/// can also remove a comment whose record is gone or whose type is no longer registered, since
/// nobody else can reach it. A holder of the legacy role names counts as holding both capabilities
/// while Auth:LegacyRoleFallback is on, the same as on Post.
/// </summary>
internal sealed class Endpoint(
    IDocumentSession session,
    CommentTargets targets,
    IPermissionResolver permissions,
    IConfiguration configuration,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant) : Endpoint<Request>
{
    public override void Configure()
    {
        Delete("/api/comments/{id}");
        Definition.ChecksCapability(CommentsCapabilities.PostComments);
        Definition.ChecksCapability(CommentsCapabilities.ManageComments);
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirst("UserId")?.Value, out var userId))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        var legacy = configuration.GetValue(CapabilityGateProcessor.LegacyRoleFallbackKey, false)
            && CommentsCapabilities.Defaults.LegacyRoles.Any(User.IsInRole);
        var canManage = legacy
            || await permissions.HasCapabilityAsync(userId, CommentsCapabilities.ManageComments, ct);
        if (!canManage && !await permissions.HasCapabilityAsync(userId, CommentsCapabilities.PostComments, ct))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        var comment = await session.LoadAsync<Comment>(req.Id, ct);
        if (comment is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (!await MayProceedAsync(comment, canManage, ct))
        {
            return;
        }

        var type = comment.TargetType;
        var targetId = comment.TargetId;
        var thread = await session.Query<Comment>()
            .Where(c => c.TargetType == type && c.TargetId == targetId)
            .ToListAsync(ct);
        var doomed = Subtree(comment, thread);
        var othersOwn = doomed.Any(c => c.AuthorId != userId);

        if (!canManage && othersOwn)
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        foreach (var c in doomed)
        {
            session.Delete(c);
        }

        if (othersOwn)
        {
            await barakoCMS.Infrastructure.Audit.AuditLog.RecordAsync(
                session,
                tenant.Slug,
                "comment.moderated",
                userId,
                User.FindFirst("Username")?.Value ?? string.Empty,
                targetType: type,
                targetId: targetId.ToString(),
                metadata: new Dictionary<string, object>
                {
                    ["commentId"] = comment.Id,
                    ["count"] = doomed.Count,
                },
                ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
                ct: ct);
        }

        await session.SaveChangesAsync(ct);
        await Send.NoContentAsync(ct);
    }

    /// <summary>
    /// Sends the refusal and returns false when the caller may not go on. A moderator is let through
    /// when the record is gone or its type unregistered, because the read check cannot be asked.
    /// </summary>
    private async Task<bool> MayProceedAsync(Comment comment, bool canManage, CancellationToken ct)
    {
        var target = targets.Find(comment.TargetType);
        var access = target is null
            ? CommentTargetAccess.NotFound
            : await target.CheckReadAsync(comment.TargetId, User, ct);

        switch (access)
        {
            case CommentTargetAccess.NotFound when canManage:
            case CommentTargetAccess.Allowed:
                return true;
            case CommentTargetAccess.Forbidden:
                await Send.ForbiddenAsync(ct);
                return false;
            default:
                await Send.NotFoundAsync(ct);
                return false;
        }
    }

    private static List<Comment> Subtree(Comment root, IReadOnlyList<Comment> thread)
    {
        var children = thread
            .Where(c => c.ParentId is not null)
            .ToLookup(c => c.ParentId!.Value);
        var found = new List<Comment>();
        var seen = new HashSet<Guid>();
        var pending = new Stack<Comment>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var next = pending.Pop();
            if (!seen.Add(next.Id))
            {
                continue;
            }

            found.Add(next);
            foreach (var child in children[next.Id])
            {
                pending.Push(child);
            }
        }

        return found;
    }
}
