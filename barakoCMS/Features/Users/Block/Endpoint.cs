using FastEndpoints;
using Marten;
using Marten.Patching;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;

namespace barakoCMS.Features.Users.Block;

/// <summary>
/// POST /api/users/{userId}/block: a global SuperAdmin blocks an account. The user cannot sign in
/// by any path, their sessions and access tokens end, and their API keys stop working until
/// <c>/unblock</c>. Not for yourself or another SuperAdmin. Blocking a blocked user changes nothing.
/// </summary>
[barakoCMS.Infrastructure.Filters.NoIdempotentReplay]
internal class Endpoint(
    IDocumentSession session,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant) : Endpoint<Request>
{
    internal const string SuperAdminOnlyMessage = "Only a platform administrator (SuperAdmin) can block an account.";
    internal const string SelfMessage = "You cannot block your own account.";
    internal const string SuperAdminTargetMessage = "A SuperAdmin cannot be blocked.";

    public override void Configure()
    {
        Post("/api/users/{userId}/block");
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

        await BlockGuard.LockAsync(session, req.UserId, ct);
        var user = await session.LoadAsync<User>(req.UserId, ct);
        if (user is null)
        {
            AddError(BlockGuard.UnknownUserMessage);
            await Send.ErrorsAsync(404, ct);
            return;
        }

        if (await BlockGuard.HoldsSuperAdminAsync(session, user, ct))
        {
            AddError(SuperAdminTargetMessage);
            await Send.ErrorsAsync(403, ct);
            return;
        }

        // Revoked on every call, a repeat included: a sign-in that committed its refresh row after
        // the first block's query would otherwise stay alive.
        await RevokeRefreshTokens.ForUserAsync(session, user.Id, "Account blocked", ct, Resolve<ISessionEpochService>());
        if (user.BlockedAt is null)
        {
            session.Patch<User>(user.Id).Set(u => u.BlockedAt, DateTime.UtcNow);
            session.Patch<User>(user.Id).Set(u => u.BlockedBy, (Guid?)actorId);
            await AuditLog.RecordAsync(session, tenant.Slug, AccountBlock.BlockedAuditAction, actorId,
                User.FindFirst("Username")?.Value, targetType: "User", targetId: user.Id.ToString(), ct: ct);
        }

        await session.SaveChangesAsync(ct);
        await Send.OkAsync(ct);
    }
}
