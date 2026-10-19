using barakoCMS.Infrastructure.Auth;
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
internal sealed class RemoveExternalIdentityEndpoint(IDocumentSession session)
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

        session.Delete(link);
        await session.SaveChangesAsync(ct);
        await Send.NoContentAsync(ct);
    }
}
