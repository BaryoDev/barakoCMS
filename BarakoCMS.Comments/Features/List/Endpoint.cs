using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace BarakoCMS.Comments.Features.List;

/// <summary>
/// GET /api/comments?targetType=&amp;targetId=: the thread on one record, oldest first, one page at a
/// time. Replies carry their <c>parentId</c>, so the client draws the nesting.
/// </summary>
internal sealed class Endpoint(IQuerySession session, CommentTargets targets) : Endpoint<Request, PaginatedResponse<Item>>
{
    public override void Configure()
    {
        Get("/api/comments");
        Definition.RequireCapability(CommentsCapabilities.ReadComments, CommentsCapabilities.Defaults.LegacyRoles);
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
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

        var type = target.Type;
        var page = await session.Query<Comment>()
            .Where(c => c.TargetType == type && c.TargetId == req.TargetId)
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .ToPagedResponseAsync(req, ct);

        await Send.OkAsync(new PaginatedResponse<Item>
        {
            Items = page.Items.Select(Item.From).ToList(),
            Page = page.Page,
            PageSize = page.PageSize,
            TotalItems = page.TotalItems,
        }, ct);
    }
}
