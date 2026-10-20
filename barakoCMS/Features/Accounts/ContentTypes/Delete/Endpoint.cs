using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.Accounts.ContentTypes.Delete;

/// <summary>DELETE /api/accounts/{slug}/content-types/{name}: remove an account-owned type.</summary>
/// <remarks>
/// Refused while an entry of the type exists in any tenant of the account (visible only on the
/// request's own tenant under <c>Tenancy:DatabaseEnforcement</c>), since the entries would be left
/// with no type to validate them.
/// </remarks>
internal sealed class Endpoint(
    IDocumentSession session,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Delete("/api/accounts/{slug}/content-types/{name}");
        Definition.ChecksCapability(SystemCapabilities.ManageTenants);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var account = await AccountOwnedTypes.ManagedAsync(
            HttpContext, session, Route<string>("slug") ?? string.Empty,
            () => Send.ForbiddenAsync(ct), () => Send.NotFoundAsync(ct), ct);
        if (account is null) return;

        var name = barakoCMS.Core.ContentTypeName.Normalize(Route<string>("name"));
        var owned = await session.Query<AccountContentType>()
            .FirstOrDefaultAsync(t => t.AccountId == account.Id && t.Name == name, ct);
        if (owned is null) { await Send.NotFoundAsync(ct); return; }

        var tenantIds = await AccountContentTypes.TenantIdsAsync(session, account.Id, ct);
        var entries = await session.Query<Models.Content>()
            .CountAsync(c => c.TenantIsOneOf(tenantIds.ToArray()) && c.ContentType == name, ct);
        if (entries > 0)
        {
            AddError($"\"{name}\" has {entries} {(entries == 1 ? "entry" : "entries")} in the account's tenants. Remove them first.");
            ThrowIfAnyErrors(StatusCodes.Status409Conflict);
        }

        session.Delete(owned);
        await AccountTypeAudit.RecordAsync(session, User, tenant.Slug, "accountcontenttype.deleted", account, name, null, ct);
        await session.SaveChangesAsync(ct);
        await Send.NoContentAsync(ct);
    }
}
