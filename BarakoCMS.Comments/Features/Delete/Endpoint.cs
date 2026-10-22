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
/// DELETE /api/comments/{id}: removes a comment and every reply under it. The author may, and so
/// may a caller holding manage_comments, and both only while they can still read the record.
/// </summary>
internal sealed class Endpoint(IDocumentSession session, CommentTargets targets, IPermissionResolver permissions)
    : Endpoint<Request>
{
    public override void Configure()
    {
        Delete("/api/comments/{id}");
        Definition.RequireCapability(CommentsCapabilities.PostComments, CommentsCapabilities.Defaults.LegacyRoles);
        Definition.ChecksCapability(CommentsCapabilities.ManageComments);
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirst("UserId")?.Value, out var userId))
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

        if (comment.AuthorId != userId && !await permissions.HasCapabilityAsync(userId, CommentsCapabilities.ManageComments, ct))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        var type = comment.TargetType;
        var targetId = comment.TargetId;
        var thread = await session.Query<Comment>()
            .Where(c => c.TargetType == type && c.TargetId == targetId)
            .ToListAsync(ct);

        foreach (var doomed in Subtree(comment.Id, thread))
        {
            session.Delete(doomed);
        }

        await session.SaveChangesAsync(ct);
        await Send.NoContentAsync(ct);
    }

    private static List<Comment> Subtree(Guid rootId, IReadOnlyList<Comment> thread)
    {
        var ids = new HashSet<Guid> { rootId };
        var found = new List<Comment>();
        bool grew;
        do
        {
            grew = false;
            foreach (var c in thread)
            {
                if ((c.Id == rootId || (c.ParentId is { } p && ids.Contains(p))) && !found.Contains(c))
                {
                    found.Add(c);
                    ids.Add(c.Id);
                    grew = true;
                }
            }
        }
        while (grew);
        return found;
    }
}
