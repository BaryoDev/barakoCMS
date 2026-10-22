using barakoCMS.Infrastructure.Auth;
using FastEndpoints;
using Marten;

namespace BarakoCMS.Comments.Features.Post;

/// <summary>POST /api/comments: a comment, or a reply, on a record the caller can read.</summary>
internal sealed class Endpoint(IDocumentSession session, CommentTargets targets) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Post("/api/comments");
        Definition.RequireCapability(CommentsCapabilities.PostComments, CommentsCapabilities.Defaults.LegacyRoles);
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirst("UserId")?.Value, out var authorId))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        var target = targets.Find(req.TargetType);
        if (target is null)
        {
            AddError(r => r.TargetType, "No such kind of record takes comments.");
            await Send.ErrorsAsync(400, ct);
            return;
        }

        switch (await target.CheckReadAsync(req.TargetId, User, ct))
        {
            case CommentTargetAccess.NotFound:
                await Send.NotFoundAsync(ct);
                return;
            case CommentTargetAccess.Forbidden:
                await Send.ForbiddenAsync(ct);
                return;
        }

        if (req.ParentId is { } parentId)
        {
            var parent = await session.LoadAsync<Comment>(parentId, ct);
            if (parent is null || parent.TargetId != req.TargetId
                || !string.Equals(parent.TargetType, target.Type, StringComparison.OrdinalIgnoreCase))
            {
                AddError(r => r.ParentId, "The comment being replied to is not on this record.");
                await Send.ErrorsAsync(400, ct);
                return;
            }
        }

        var author = await session.LoadAsync<barakoCMS.Models.User>(authorId, ct);
        var comment = new Comment
        {
            Id = Guid.NewGuid(),
            TargetType = target.Type,
            TargetId = req.TargetId,
            ParentId = req.ParentId,
            AuthorId = authorId,
            AuthorName = author is null ? string.Empty : author.DisplayName ?? author.Username,
            Body = req.Body,
            CreatedAt = DateTime.UtcNow,
        };

        session.Store(comment);
        await session.SaveChangesAsync(ct);

        await Send.ResponseAsync(Response.From(comment), 201, ct);
    }
}
