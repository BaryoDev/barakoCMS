using FastEndpoints;
using Marten;
using Marten.Patching;
using barakoCMS.Features.Users.Block;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;

namespace barakoCMS.Features.Users.Unblock;

/// <summary>
/// POST /api/users/{userId}/unblock: a global SuperAdmin lifts a block. Sessions and tokens the
/// block ended stay ended, and the lockout fields are not touched. A user who is not blocked is
/// left as is.
/// </summary>
[barakoCMS.Infrastructure.Filters.NoIdempotentReplay]
internal class Endpoint(
    IDocumentSession session,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant,
    IConfiguration configuration) : Endpoint<Request>
{
    internal const string SuperAdminOnlyMessage = "Only a platform administrator (SuperAdmin) can unblock an account.";
    internal const string SelfMessage = "You cannot change the block on your own account.";

    public override void Configure()
    {
        Post("/api/users/{userId}/unblock");
        Definition.RequireCapability(SystemCapabilities.ManageUsers, "SuperAdmin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var (actorId, status, refusal) = await BlockGuard.ActorAsync(session, User, req.UserId, SuperAdminOnlyMessage, SelfMessage, ct);
        if (refusal is not null)
        {
            AddError(refusal);
            await Send.ErrorsAsync(status, ct);
            return;
        }

        var user = await session.LoadAsync<User>(req.UserId, ct);
        if (user is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (user.BlockedAt is not null)
        {
            session.Patch<User>(user.Id).Set(u => u.BlockedAt, (DateTime?)null);
            session.Patch<User>(user.Id).Set(u => u.BlockedBy, (Guid?)null);
            session.Patch<User>(user.Id).Set(u => u.TokensValidFrom, (DateTime?)AccountBlock.NextWholeSecond(DateTime.UtcNow));
            await AuditLog.RecordAsync(session, tenant.Slug, AccountBlock.UnblockedAuditAction, actorId,
                User.FindFirst("Username")?.Value, targetType: "User", targetId: user.Id.ToString(), ct: ct);
            await session.SaveChangesAsync(ct);
            Resolve<ISessionEpochService>().Invalidate(user.Id);
        }

        await Send.OkAsync(ct);
    }
}
