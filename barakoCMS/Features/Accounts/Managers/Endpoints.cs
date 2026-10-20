using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FastEndpoints;
using FluentValidation;
using Marten;

namespace barakoCMS.Features.Accounts.Managers;

internal class Request : PaginatedRequest
{
}

internal sealed record ManagerResponse(Guid UserId, string Username, DateTimeOffset GrantedAt);

internal sealed class GrantRequest
{
    public Guid UserId { get; set; }
}

internal sealed class GrantRequestValidator : Validator<GrantRequest>
{
    public GrantRequestValidator() => RuleFor(r => r.UserId).NotEmpty();
}

internal static class ManagerAudit
{
    public const string Granted = "account.manager_granted";
    public const string Revoked = "account.manager_revoked";

    /// <summary>Stages the entry on the session the grant is written in, in the caller's current tenant's log.</summary>
    public static Task RecordAsync(
        IDocumentSession session, System.Security.Claims.ClaimsPrincipal actor, string action,
        TenantAccount account, Guid userId, CancellationToken ct)
    {
        Guid.TryParse(actor.FindFirst("UserId")?.Value, out var actorId);
        var tenant = (actor.FindFirst("tenant")?.Value ?? Tenant.DefaultSlug).Trim().ToLowerInvariant();
        return AuditLog.RecordAsync(session, tenant, action, actorId, actor.FindFirst("Username")?.Value,
            targetType: "User", targetId: userId.ToString(),
            metadata: new Dictionary<string, object>
            {
                ["accountId"] = account.Id.ToString(),
                ["accountSlug"] = account.Slug,
            },
            ct: ct);
    }
}

/// <summary>GET /api/accounts/{slug}/managers: who may manage the tenants of the account (platform manager only).</summary>
internal class ListEndpoint(IQuerySession session) : Endpoint<Request, PaginatedResponse<ManagerResponse>>
{
    public override void Configure()
    {
        Get("/api/accounts/{slug}/managers");
        Definition.ChecksCapability(SystemCapabilities.ManageTenants);
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var reach = await AccountReach.ForAsync(HttpContext, session, ct);
        if (reach is not { IsGlobal: true }) { await Send.ForbiddenAsync(ct); return; }

        var account = await DefaultAccount.FindAsync(session, Route<string>("slug") ?? string.Empty, ct);
        if (account is null) { await Send.NotFoundAsync(ct); return; }

        var page = await session.Query<TenantAccountManager>()
            .Where(m => m.AccountId == account.Id)
            .OrderBy(m => m.GrantedAt)
            .ToPagedResponseAsync(req, ct);

        var ids = page.Items.Select(m => m.UserId).ToList();
        var names = (await session.Query<User>().Where(u => ids.Contains(u.Id)).ToListAsync(ct))
            .ToDictionary(u => u.Id, u => u.Username);

        await Send.OkAsync(new PaginatedResponse<ManagerResponse>
        {
            Items = page.Items.Select(m => new ManagerResponse(
                m.UserId, names.GetValueOrDefault(m.UserId, string.Empty),
                new DateTimeOffset(DateTime.SpecifyKind(m.GrantedAt, DateTimeKind.Utc)))).ToList(),
            Page = page.Page,
            PageSize = page.PageSize,
            TotalItems = page.TotalItems,
        }, ct);
    }
}

/// <summary>POST /api/accounts/{slug}/managers: let a user manage the account's tenants (platform manager only).</summary>
/// <remarks>Granting twice is not an error and changes nothing.</remarks>
internal class GrantEndpoint(IDocumentSession session) : Endpoint<GrantRequest, ManagerResponse>
{
    public override void Configure()
    {
        Post("/api/accounts/{slug}/managers");
        Definition.ChecksCapability(SystemCapabilities.ManageTenants);
    }

    public override async Task HandleAsync(GrantRequest req, CancellationToken ct)
    {
        var reach = await AccountReach.ForAsync(HttpContext, session, ct);
        if (reach is not { IsGlobal: true }) { await Send.ForbiddenAsync(ct); return; }

        var account = await DefaultAccount.FindAsync(session, Route<string>("slug") ?? string.Empty, ct);
        if (account is null) { await Send.NotFoundAsync(ct); return; }

        var user = await session.LoadAsync<User>(req.UserId, ct);
        if (user is null) { AddError(r => r.UserId, "No such user."); ThrowIfAnyErrors(); }

        var grant = await session.Query<TenantAccountManager>()
            .FirstOrDefaultAsync(m => m.AccountId == account.Id && m.UserId == req.UserId, ct);
        if (grant is null)
        {
            grant = new TenantAccountManager { Id = Guid.NewGuid(), AccountId = account.Id, UserId = req.UserId };
            session.Store(grant);
            await ManagerAudit.RecordAsync(session, User, ManagerAudit.Granted, account, req.UserId, ct);
            await session.SaveChangesAsync(ct);
        }

        await Send.OkAsync(new ManagerResponse(
            grant.UserId, user!.Username, new DateTimeOffset(DateTime.SpecifyKind(grant.GrantedAt, DateTimeKind.Utc))), ct);
    }
}

/// <summary>DELETE /api/accounts/{slug}/managers/{userId}: take the grant away (platform manager only).</summary>
internal class RevokeEndpoint(IDocumentSession session) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Delete("/api/accounts/{slug}/managers/{userId}");
        Definition.ChecksCapability(SystemCapabilities.ManageTenants);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var reach = await AccountReach.ForAsync(HttpContext, session, ct);
        if (reach is not { IsGlobal: true }) { await Send.ForbiddenAsync(ct); return; }

        var account = await DefaultAccount.FindAsync(session, Route<string>("slug") ?? string.Empty, ct);
        if (account is null || !Guid.TryParse(Route<string>("userId"), out var userId))
        { await Send.NotFoundAsync(ct); return; }

        var grant = await session.Query<TenantAccountManager>()
            .FirstOrDefaultAsync(m => m.AccountId == account.Id && m.UserId == userId, ct);
        if (grant is null) { await Send.NotFoundAsync(ct); return; }

        session.Delete(grant);
        await ManagerAudit.RecordAsync(session, User, ManagerAudit.Revoked, account, userId, ct);
        await session.SaveChangesAsync(ct);
        await Send.NoContentAsync(ct);
    }
}
