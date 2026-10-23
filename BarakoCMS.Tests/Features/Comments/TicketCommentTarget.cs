using System.Collections.Concurrent;
using System.Security.Claims;
using BarakoCMS.Comments;

namespace BarakoCMS.Tests.Features.Comments;

/// <summary>
/// A second kind of record, standing in for a helpdesk ticket, to show the comment surface is not
/// tied to content entries. A test adds an id here and says whether the caller may read it.
/// </summary>
internal sealed class TicketCommentTarget : ICommentTarget
{
    public static readonly TicketCommentTarget Instance = new();

    private readonly ConcurrentDictionary<Guid, bool> _tickets = new();

    public string Type => "ticket";

    public void Add(Guid id, bool readable) => _tickets[id] = readable;

    public void Remove(Guid id) => _tickets.TryRemove(id, out _);

    public Task<CommentTargetAccess> CheckReadAsync(Guid targetId, ClaimsPrincipal caller, CancellationToken ct) =>
        Task.FromResult(!_tickets.TryGetValue(targetId, out var readable)
            ? CommentTargetAccess.NotFound
            : readable ? CommentTargetAccess.Allowed : CommentTargetAccess.Forbidden);
}
