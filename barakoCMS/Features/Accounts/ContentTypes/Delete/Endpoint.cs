using barakoCMS.Features.ApiKeys;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.Accounts.ContentTypes.Delete;

/// <summary>Sent in place of 204 when live API keys of the caller's tenant name the removed type.</summary>
internal sealed record DeleteResponse(List<ApiKeyWarning> ApiKeyWarnings);

/// <summary>DELETE /api/accounts/{slug}/content-types/{name}: remove an account-owned type.</summary>
/// <remarks>
/// The delete goes through either way. When live API keys of the caller's tenant (not revoked, not
/// expired) name the type, the answer is 200 with <c>apiKeyWarnings</c> listing them (id, name,
/// prefix, the matching types), since those keys can no longer push to it. Otherwise 204.
/// 
/// Refused while an entry of the type exists in any tenant of the account, counted on a session
/// opened for each tenant so it holds under <c>Tenancy:DatabaseEnforcement</c>, since the entries
/// would be left with no type to validate them. A tenant that cannot be read fails the request
/// rather than counting as empty.
/// </remarks>
internal sealed class Endpoint(
    IDocumentSession session,
    IDocumentStore store,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant) : EndpointWithoutRequest<DeleteResponse>
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
        var entries = (await AccountContentTypes.EntriesPerTenantAsync(store, tenantIds, [name], ct)).Values.Sum();
        if (entries > 0)
        {
            AddError($"\"{name}\" has {entries} {(entries == 1 ? "entry" : "entries")} in the account's tenants. Remove them first.");
            ThrowIfAnyErrors(StatusCodes.Status409Conflict);
        }

        session.Delete(owned);
        await AccountTypeAudit.RecordAsync(session, User, tenant.Slug, "accountcontenttype.deleted", account, name, null, ct);
        var warnings = await ApiKeyContentTypeImpact.KeysNamingAsync(session, tenant.Slug, [name], ct);
        await session.SaveChangesAsync(ct);
        if (warnings.Count == 0) await Send.NoContentAsync(ct);
        else await Send.OkAsync(new DeleteResponse(warnings), ct);
    }
}
