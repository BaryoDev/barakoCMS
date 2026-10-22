using System.Security.Claims;
using barakoCMS.Infrastructure.Services;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Comments;

/// <summary>What a caller may do with a record's comments, which is whatever they may do with the record.</summary>
public enum CommentTargetAccess
{
    /// <summary>No such record in this tenant.</summary>
    NotFound,

    /// <summary>The record exists and the caller cannot read it.</summary>
    Forbidden,

    /// <summary>The caller can read the record.</summary>
    Allowed,
}

/// <summary>
/// A kind of record that can carry comments. Register one with
/// <c>services.AddScoped&lt;ICommentTarget, MyTarget&gt;()</c>, and the comment endpoints accept its
/// <see cref="Type"/>.
/// </summary>
/// <remarks>
/// The module cannot know how another module decides who may read its records, so it asks. Every
/// read and every post goes through <see cref="CheckReadAsync"/> first, and a caller who is not
/// <see cref="CommentTargetAccess.Allowed"/> gets nothing, whatever comment capability they hold.
/// The request is already scoped to one tenant, so an implementation that loads the record from the
/// request's session cannot see another tenant's.
/// </remarks>
public interface ICommentTarget
{
    /// <summary>The name callers use for this kind of record. Compared without regard to case.</summary>
    string Type { get; }

    Task<CommentTargetAccess> CheckReadAsync(Guid targetId, ClaimsPrincipal caller, CancellationToken ct);
}

/// <summary>Content entries. Readable to whoever the content type's read permission lets in.</summary>
internal sealed class ContentCommentTarget(IQuerySession session, IPermissionResolver permissions) : ICommentTarget
{
    public const string TypeName = "content";

    public string Type => TypeName;

    public async Task<CommentTargetAccess> CheckReadAsync(Guid targetId, ClaimsPrincipal caller, CancellationToken ct)
    {
        var content = await session.LoadAsync<barakoCMS.Models.Content>(targetId, ct);
        if (content is null)
        {
            return CommentTargetAccess.NotFound;
        }

        if (!Guid.TryParse(caller.FindFirst("UserId")?.Value, out var userId))
        {
            return CommentTargetAccess.Forbidden;
        }

        var user = await session.LoadAsync<barakoCMS.Models.User>(userId, ct);
        return user is not null && await permissions.CanPerformActionAsync(user, content.ContentType, "read", content, ct)
            ? CommentTargetAccess.Allowed
            : CommentTargetAccess.Forbidden;
    }
}

/// <summary>The kinds of record this host registered, looked up by name.</summary>
internal sealed class CommentTargets(IEnumerable<ICommentTarget> targets)
{
    private readonly Dictionary<string, ICommentTarget> _byType = Index(targets);

    public ICommentTarget? Find(string? type) =>
        type is not null && _byType.TryGetValue(type.Trim(), out var target) ? target : null;

    private static Dictionary<string, ICommentTarget> Index(IEnumerable<ICommentTarget> targets)
    {
        var byType = new Dictionary<string, ICommentTarget>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in targets)
        {
            if (!byType.TryAdd(target.Type, target))
            {
                throw new InvalidOperationException(
                    $"Two comment targets register the type '{target.Type}' ({byType[target.Type].GetType().Name} and {target.GetType().Name}). A type names one kind of record.");
            }
        }

        return byType;
    }
}

/// <summary>Builds the target index once at startup, so a duplicate type stops the host and not the first request.</summary>
internal sealed class CommentTargetsCheck(IServiceProvider services) : Microsoft.Extensions.Hosting.IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        _ = scope.ServiceProvider.GetRequiredService<CommentTargets>();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
