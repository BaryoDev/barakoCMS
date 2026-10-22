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
/// manage_comments may delete any, and both only while they can still read the record.
/// </summary>
internal sealed class Endpoint(IDocumentSession session, CommentTargets targets, IPermissionResolver permissions)
    : Endpoint<Request>
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

        var canManage = await permissions.HasCapabilityAsync(userId, CommentsCapabilities.ManageComments, ct);
        if (!canManage && !await permissions.HasCapabilityAsync(userId, CommentsCapabilities.PostComments, ct))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        var comment = await session.LoadAsync<Comment>(req.Id, ct);
        var target = comment is null ? null : targets.Find(comment.TargetType);
        if (comment is null || target is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        switch (await target.CheckReadAsync(comment.TargetId, User, ct))
        {
            case CommentTargetAccess.NotFound:
                await Send.NotFoundAsync(ct);
                return;
            case CommentTargetAccess.Forbidden:
                await Send.ForbiddenAsync(ct);
                return;
        }

        var type = comment.TargetType;
        var targetId = comment.TargetId;
        var thread = await session.Query<Comment>()
            .Where(c => c.TargetType == type && c.TargetId == targetId)
            .ToListAsync(ct);
        var doomed = Subtree(comment, thread);

        if (!canManage && doomed.Any(c => c.AuthorId != userId))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        foreach (var c in doomed)
        {
            session.Delete(c);
        }

        await session.SaveChangesAsync(ct);
        await Send.NoContentAsync(ct);
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
