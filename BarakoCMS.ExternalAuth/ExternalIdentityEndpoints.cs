using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using FastEndpoints;
using FluentValidation;
using Marten;

namespace BarakoCMS.ExternalAuth;

internal sealed class ExternalIdentitiesRequest : ListRequest
{
    public Guid UserId { get; set; }
}

internal sealed record ExternalIdentityResponse(string Id, string Provider, string Issuer, DateTime LinkedAt);

internal sealed class ExternalIdentitiesValidator : Validator<ExternalIdentitiesRequest>
{
    public ExternalIdentitiesValidator() => RuleFor(x => x.UserId).NotEmpty();
}

internal sealed class RemoveExternalIdentityRequest
{
    public Guid UserId { get; set; }

    public string Id { get; set; } = string.Empty;
}

internal sealed class RemoveExternalIdentityValidator : Validator<RemoveExternalIdentityRequest>
{
    public RemoveExternalIdentityValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.Id).NotEmpty().MaximumLength(64);
    }
}

internal static class ExternalIdentityAccess
{
    /// <summary>
    /// A link belongs to the person and works in every tenant they sign in to, so a tenant
    /// administrator may not list or remove one. Only a platform user administrator may.
    /// </summary>
    public static void RequirePlatformUserAdmin(EndpointDefinition definition) =>
        definition.RequireCapability(SystemCapabilities.ManageUsers, "SuperAdmin");

    /// <summary>
    /// Whether the caller may touch this user's links. A holder of manage_users who is not a
    /// SuperAdmin may not touch a SuperAdmin's, since removing one is a step toward taking the account.
    /// </summary>
    public static async Task<bool> MayTouchAsync(
        IQuerySession session, System.Security.Claims.ClaimsPrincipal caller, Guid targetId, CancellationToken ct)
    {
        var target = await session.LoadAsync<User>(targetId, ct);
        if (target is null || !target.RoleIds.Contains(SystemRoles.SuperAdminRoleId))
        {
            return true;
        }

        if (!Guid.TryParse(caller.FindFirst("UserId")?.Value, out var callerId))
        {
            return false;
        }

        var self = await session.LoadAsync<User>(callerId, ct);
        return self?.RoleIds.Contains(SystemRoles.SuperAdminRoleId) == true;
    }

    public static Task<bool> UserExistsAsync(IQuerySession session, Guid userId, CancellationToken ct) =>
        session.Query<User>().AnyAsync(u => u.Id == userId, ct);
}

/// <summary>GET /api/users/{userId}/external-identities: the provider accounts linked to a user.</summary>
internal sealed class ListExternalIdentitiesEndpoint(IQuerySession session)
    : Endpoint<ExternalIdentitiesRequest, PaginatedResponse<ExternalIdentityResponse>>
{
    public override void Configure()
    {
        Get("/api/users/{userId}/external-identities");
        ExternalIdentityAccess.RequirePlatformUserAdmin(Definition);
    }

    public override async Task HandleAsync(ExternalIdentitiesRequest req, CancellationToken ct)
    {
        if (!await ExternalIdentityAccess.UserExistsAsync(session, req.UserId, ct))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (!await ExternalIdentityAccess.MayTouchAsync(session, User, req.UserId, ct))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        var links = await session.Query<ExternalIdentity>()
            .Where(i => i.UserId == req.UserId)
            .OrderBy(i => i.LinkedAt)
            .ToListAsync(ct);
        var items = links.Select(i => new ExternalIdentityResponse(i.Id, i.Provider, i.Issuer, i.LinkedAt)).ToList();
        await Send.OkAsync(items.ToPagedResponse(req), ct);
    }
}

/// <summary>
/// DELETE /api/users/{userId}/external-identities/{id}: unlinks a provider account. The
/// person's next sign-in through it links again by verified email, so this cuts a stale link and
/// does not ban the account.
/// </summary>
internal sealed class RemoveExternalIdentityEndpoint(IDocumentSession session, TenantContext tenant)
    : Endpoint<RemoveExternalIdentityRequest>
{
    public override void Configure()
    {
        Delete("/api/users/{userId}/external-identities/{id}");
        ExternalIdentityAccess.RequirePlatformUserAdmin(Definition);
    }

    public override async Task HandleAsync(RemoveExternalIdentityRequest req, CancellationToken ct)
    {
        var link = await session.LoadAsync<ExternalIdentity>(req.Id, ct);
        if (link is null
            || link.UserId != req.UserId
            || !await ExternalIdentityAccess.UserExistsAsync(session, req.UserId, ct))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (!await ExternalIdentityAccess.MayTouchAsync(session, User, req.UserId, ct))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        session.Delete(link);
        Guid.TryParse(User.FindFirst("UserId")?.Value, out var actorId);
        await AuditLog.RecordAsync(session, tenant.Slug, "user.external_identity.removed", actorId,
            User.FindFirst("Username")?.Value, targetType: "User", targetId: req.UserId.ToString(),
            metadata: new Dictionary<string, object> { ["provider"] = link.Provider, ["issuer"] = link.Issuer }, ct: ct);
        await session.SaveChangesAsync(ct);
        await Send.NoContentAsync(ct);
    }
}
