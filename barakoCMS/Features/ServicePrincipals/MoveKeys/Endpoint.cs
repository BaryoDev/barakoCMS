using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Features.Roles;
using barakoCMS.Features.Tenants.Members;
using barakoCMS.Features.Users;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Models;
using barakoCMS.Infrastructure.Services;
using FastEndpoints;
using Marten;
using barakoCMS.Features.ServicePrincipals;

namespace barakoCMS.Features.ServicePrincipals.MoveKeys;

internal sealed class MoveKeysRequest
{
    public Guid ServicePrincipalId { get; set; }

    /// <summary>One key to move. Leave empty and set <see cref="AllManageable"/> to move many.</summary>
    public Guid? KeyId { get; set; }

    /// <summary>
    /// Move every active key of the tenant the caller may manage: the caller's own keys, or all of
    /// them for a SuperAdmin. This is the same reach rotation has.
    /// </summary>
    public bool AllManageable { get; set; }
}

internal sealed record MoveKeysResponse(int Moved, List<Guid> KeyIds);

/// <summary>
/// POST /api/api-keys/move-to-principal. Opt-in: nothing moves unless a caller asks. A moved key
/// keeps its secret, scopes and expiry and from then on acts as the principal, so it no longer
/// depends on the person who made it.
/// </summary>
internal class MoveKeysToPrincipalEndpoint(IDocumentSession session, ICallerCapabilities callers, IPermissionResolver permissions) : Endpoint<MoveKeysRequest, MoveKeysResponse>
{
    public override void Configure()
    {
        Post("/api/api-keys/move-to-principal");
        Definition.RequireCapability(SystemCapabilities.ManageApiKeys, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(MoveKeysRequest req, CancellationToken ct)
    {
        var tenant = PrincipalRules.TenantOf(User);
        if (req.KeyId is null == !req.AllManageable)
            ThrowError("Name one key with KeyId, or set AllManageable, not both and not neither.");

        var principal = await PrincipalRules.InTenantAsync(session, req.ServicePrincipalId, tenant, ct);
        if (principal is null || principal.Disabled)
            ThrowError(r => r.ServicePrincipalId, "That service principal does not exist in this tenant or is disabled.");
        if (await PrincipalRules.NotHeldByPrincipalAsync(session, callers, permissions, User, principal!, ct) is { } notHeld)
            ThrowError(r => r.ServicePrincipalId, notHeld, 403);

        var actor = PrincipalRules.ActorOf(User);
        var superAdmin = await PlatformRoles.IsSuperAdminAsync(session, User, ct);
        var candidates = await session.Query<ApiKey>()
            .Where(k => k.TenantSlug == tenant && !k.Revoked && k.ServicePrincipalId == null
                        && (req.KeyId == null || k.Id == req.KeyId))
            .ToListAsync(ct);
        var now = DateTime.UtcNow;
        var moving = candidates
            .Where(k => k.ExpiresAt is null || k.ExpiresAt > now)
            .Where(k => superAdmin || k.UserId == actor)
            .ToList();

        if (req.KeyId is not null && moving.Count == 0)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        foreach (var key in moving)
        {
            key.ServicePrincipalId = principal!.Id;
            session.Store(key);
        }

        if (moving.Count > 0)
            await AuditLog.RecordAsync(session, tenant, "apikey.moved_to_principal", actor, User.FindFirst("Username")?.Value,
                targetType: "ServicePrincipal", targetId: principal!.Id.ToString(),
                metadata: new() { ["name"] = principal.Name, ["keyIds"] = moving.Select(k => k.Id.ToString()).ToList() }, ct: ct);
        if (!await PrincipalRules.SaveAsync(session, this, ct)) return;

        await Send.OkAsync(new MoveKeysResponse(moving.Count, moving.Select(k => k.Id).ToList()), ct);
    }
}
