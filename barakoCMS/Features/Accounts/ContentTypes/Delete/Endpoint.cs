using barakoCMS.Features.ApiKeys;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.Accounts.ContentTypes.Delete;

internal sealed class Request
{
    /// <summary>Run the same checks as the delete and report which API keys name the type, deleting nothing.</summary>
    [QueryParam]
    public bool DryRun { get; set; }
}

/// <summary>What a dry run answers. A real delete answers 204 with no body.</summary>
/// <param name="DryRun">Always true.</param>
/// <param name="ApiKeysAffected">How many live keys name the type, across every tenant of the account.</param>
/// <param name="ApiKeyWarnings">The keys of the caller's tenant that name it, or empty when the caller may not read API keys.</param>
internal sealed record Response(bool DryRun, int ApiKeysAffected, List<ApiKeyWarning> ApiKeyWarnings)
    : barakoCMS.Core.Validation.IDryRunResponse;

/// <summary>DELETE /api/accounts/{slug}/content-types/{name}: remove an account-owned type.</summary>
/// <remarks>
/// With <c>?dryRun=true</c> it runs the same checks and deletes nothing, answering 200 with how many
/// live API keys (not revoked, not expired) name the type in any tenant of the account, since the
/// delete removes the type for all of them. The keys themselves (id, name, prefix, types) are
/// listed only for the caller's tenant and only for a caller who may read API keys, since the delete
/// is gated on ManageTenants and key names are key data. Without it the answer is 204.
/// Refused while an entry of the type exists in any tenant of the account, counted on a session
/// opened for each tenant so it holds under <c>Tenancy:DatabaseEnforcement</c>, since the entries
/// would be left with no type to validate them. A tenant that cannot be read fails the request
/// rather than counting as empty.
/// </remarks>
internal sealed class Endpoint(
    IDocumentSession session,
    IDocumentStore store,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Delete("/api/accounts/{slug}/content-types/{name}");
        Definition.ChecksCapability(SystemCapabilities.ManageTenants);
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
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

        if (req.DryRun)
        {
            var slugs = tenantIds.Select(TenantScopes.SlugFor).ToList();
            var impact = await ApiKeyContentTypeImpact.ImpactAsync(session, slugs, tenant.Slug, [name], ct);
            var mayRead = await CapabilityGateProcessor.HoldsAsync(HttpContext, ListApiKeysEndpoint.Gate, ct);
            await Send.OkAsync(new Response(true, impact.Count, mayRead ? impact.Keys : []), ct);
            return;
        }

        session.Delete(owned);
        await AccountTypeAudit.RecordAsync(session, User, tenant.Slug, "accountcontenttype.deleted", account, name, null, ct);
        await session.SaveChangesAsync(ct);
        await Send.NoContentAsync(ct);
    }
}
