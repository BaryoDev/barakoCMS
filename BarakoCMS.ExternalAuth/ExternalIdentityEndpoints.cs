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
    /// Whether the person is a current member of the tenant. A link belongs to the person, not to a
    /// tenant, so membership is the only reason a tenant's administrator may see or remove one. A
    /// stranger answers as if they did not exist.
    /// </summary>
    public static Task<bool> IsMemberAsync(IQuerySession session, Guid userId, string slug, CancellationToken ct) =>
        session.Query<Membership>().AnyAsync(
            m => m.UserId == userId && m.TenantSlug == slug && m.Status != MembershipStatus.Removed, ct);
}

/// <summary>GET /api/tenants/members/{userId}/external-identities: the provider accounts linked to a member.</summary>
internal sealed class ListExternalIdentitiesEndpoint(IQuerySession session, TenantContext tenant)
    : Endpoint<ExternalIdentitiesRequest, PaginatedResponse<ExternalIdentityResponse>>
{
    public override void Configure()
    {
        Get("/api/tenants/members/{userId}/external-identities");
        Definition.RequireCapability(SystemCapabilities.ManageTenantMembers, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(ExternalIdentitiesRequest req, CancellationToken ct)
    {
        if (!await ExternalIdentityAccess.IsMemberAsync(session, req.UserId, tenant.Slug, ct))
        {
            await Send.NotFoundAsync(ct);
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
/// DELETE /api/tenants/members/{userId}/external-identities/{id}: unlinks a provider account. The
/// person's next sign-in through it links again by verified email, so this cuts a stale link and
/// does not ban the account.
/// </summary>
internal sealed class RemoveExternalIdentityEndpoint(IDocumentSession session, TenantContext tenant)
    : Endpoint<RemoveExternalIdentityRequest>
{
    public override void Configure()
    {
        Delete("/api/tenants/members/{userId}/external-identities/{id}");
        Definition.RequireCapability(SystemCapabilities.ManageTenantMembers, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(RemoveExternalIdentityRequest req, CancellationToken ct)
    {
        var link = await session.LoadAsync<ExternalIdentity>(req.Id, ct);
        if (link is null
            || link.UserId != req.UserId
            || !await ExternalIdentityAccess.IsMemberAsync(session, req.UserId, tenant.Slug, ct))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        session.Delete(link);
        await session.SaveChangesAsync(ct);
        await Send.NoContentAsync(ct);
    }
}
