using barakoCMS.Models;
using Marten;

namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// Finds the content types an account owns for the tenant a session is open on.
/// </summary>
/// <remarks>
/// Read after the tenant's own definition and only when it finds none, so a deployment with no
/// account-owned type pays for one extra lookup on an entry whose type is unknown, and nothing else.
/// </remarks>
internal static class AccountContentTypes
{
    public static string TenantSlugOf(IQuerySession session) =>
        session.TenantId == JasperFx.StorageConstants.DefaultTenantId ? Tenant.DefaultSlug : session.TenantId;

    public static async Task<Guid> AccountOfAsync(IQuerySession session, string tenantSlug, CancellationToken ct = default)
    {
        var accountId = await session.Query<Tenant>()
            .Where(t => t.Slug == tenantSlug)
            .Select(t => t.AccountId)
            .FirstOrDefaultAsync(ct);

        return accountId ?? TenantAccount.DefaultId;
    }

    /// <summary>The account-owned type of this name that the session's tenant uses, or null.</summary>
    public static async Task<AccountContentType?> FindAsync(IQuerySession session, string name, CancellationToken ct = default)
    {
        var accountId = await AccountOfAsync(session, TenantSlugOf(session), ct);
        return await session.Query<AccountContentType>()
            .FirstOrDefaultAsync(t => t.AccountId == accountId && t.Name == name, ct);
    }

    public static async Task<ContentTypeDefinition?> FindDefinitionAsync(
        IQuerySession session, string name, CancellationToken ct = default) =>
        (await FindAsync(session, name, ct))?.Definition;

    /// <summary>Every account-owned type the session's tenant uses.</summary>
    public static async Task<(Guid AccountId, IReadOnlyList<AccountContentType> Types)> ForTenantAsync(
        IQuerySession session, CancellationToken ct = default)
    {
        var accountId = await AccountOfAsync(session, TenantSlugOf(session), ct);
        var types = await session.Query<AccountContentType>()
            .Where(t => t.AccountId == accountId)
            .ToListAsync(ct);
        return (accountId, types);
    }

    /// <summary>The Marten tenant ids of an account's tenants, the default tenant under its own id.</summary>
    public static async Task<List<string>> TenantIdsAsync(IQuerySession session, Guid accountId, CancellationToken ct = default)
    {
        var slugs = accountId == TenantAccount.DefaultId
            ? await session.Query<Tenant>().Where(t => t.AccountId == null).Select(t => t.Slug).ToListAsync(ct)
            : await session.Query<Tenant>().Where(t => t.AccountId == accountId).Select(t => t.Slug).ToListAsync(ct);

        var ids = slugs.Select(s => s == Tenant.DefaultSlug ? JasperFx.StorageConstants.DefaultTenantId : s).ToList();
        if (accountId == TenantAccount.DefaultId && !ids.Contains(JasperFx.StorageConstants.DefaultTenantId))
            ids.Add(JasperFx.StorageConstants.DefaultTenantId);
        return ids;
    }
}
