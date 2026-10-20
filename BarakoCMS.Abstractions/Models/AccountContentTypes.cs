using barakoCMS.Models;
using Marten;

namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// Finds the content types an account owns for the tenant a session is open on.
/// </summary>
/// <remarks>
/// The account-owned definition wins over a tenant's own of the same name, so nothing a tenant
/// stores by import, blueprint, collection push or seeder can loosen its Sensitivity or
/// VisibleToRoles. Every reader that needs "the definition of this name" goes through
/// <see cref="ResolveAsync"/> or <see cref="ResolveManyAsync"/>.
/// </remarks>
public static class AccountContentTypes
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

    /// <summary>
    /// The definition of a name for the session's tenant: the account's when it owns the name,
    /// otherwise the tenant's own, otherwise null.
    /// </summary>
    public static async Task<ContentTypeDefinition?> ResolveAsync(IQuerySession session, string name, CancellationToken ct = default)
    {
        if (await FindDefinitionAsync(session, name, ct) is { } shared) return shared;
        return await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name == name, ct);
    }

    /// <summary>The definitions of these names, keyed by name, the account's winning over the tenant's own.</summary>
    public static async Task<Dictionary<string, ContentTypeDefinition>> ResolveManyAsync(
        IQuerySession session, IEnumerable<string> names, CancellationToken ct = default)
    {
        var wanted = names.Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.Ordinal).ToArray();
        var result = new Dictionary<string, ContentTypeDefinition>(StringComparer.Ordinal);
        if (wanted.Length == 0) return result;

        foreach (var own in await session.Query<ContentTypeDefinition>().Where(d => wanted.Contains(d.Name)).ToListAsync(ct))
            result[own.Name] = own;

        var (_, shared) = await ForTenantAsync(session, ct);
        foreach (var type in shared.Where(t => wanted.Contains(t.Name)))
            result[type.Name] = type.Definition;

        return result;
    }

    /// <summary>
    /// Entries of a type per tenant, read on a session opened for each tenant id so the answer holds
    /// under <c>Tenancy:DatabaseEnforcement</c>, where one session sees one tenant. A failure to open
    /// or read a tenant propagates: a count that cannot be made is not a count of zero.
    /// </summary>
    public static async Task<Dictionary<string, int>> EntriesPerTenantAsync(
        IDocumentStore store, IEnumerable<string> tenantIds, IReadOnlyCollection<string> typeNames, CancellationToken ct = default)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var names = typeNames.ToArray();
        foreach (var id in tenantIds.Distinct(StringComparer.Ordinal))
        {
            await using var tenantSession = store.QuerySession(id);
            var count = await tenantSession.Query<Content>().CountAsync(c => names.Contains(c.ContentType), ct);
            if (count > 0) counts[id] = count;
        }

        return counts;
    }

    /// <summary>True when any of these tenants has decided this name as event sourced.</summary>
    public static async Task<bool> AnyEventSourcedAsync(
        IDocumentStore store, IEnumerable<string> tenantIds, string name, CancellationToken ct = default)
    {
        foreach (var id in tenantIds.Distinct(StringComparer.Ordinal))
        {
            await using var tenantSession = store.QuerySession(id);
            var policy = await tenantSession.LoadAsync<ContentTypeSourcingPolicy>(name, ct);
            if (policy is { EventSourced: true }) return true;
        }

        return false;
    }

    /// <summary>
    /// The message for refusing a tenant-local type whose name an account of that tenant owns, or null
    /// when it owns none. The one place the create endpoint, the blueprint apply and the import ask.
    /// </summary>
    public static async Task<string?> ClashMessageAsync(IQuerySession session, string name, CancellationToken ct = default) =>
        await FindAsync(session, name, ct) is null
            ? null
            : $"The account owns a Content Type named \"{name}\", so a tenant cannot define its own with that name.";

    /// <summary>
    /// Why a tenant cannot move from one account to another, or null when it can. A tenant leaving an
    /// account would orphan its entries of that account's types, and one arriving would meet an
    /// account type of the same name as a type of its own. Both are read on the tenant's own
    /// partition, so the answer holds under <c>Tenancy:DatabaseEnforcement</c>.
    /// </summary>
    public static async Task<string?> MoveRefusalAsync(
        IDocumentStore store, IQuerySession session, string tenantSlug, Guid from, Guid to, CancellationToken ct = default)
    {
        var tenantId = tenantSlug == Tenant.DefaultSlug ? JasperFx.StorageConstants.DefaultTenantId : tenantSlug;
        await using var tenantSession = store.QuerySession(tenantId);

        var leaving = (await session.Query<AccountContentType>().Where(t => t.AccountId == from).ToListAsync(ct))
            .Select(t => t.Name).ToArray();
        if (leaving.Length > 0)
        {
            var entries = await tenantSession.Query<Content>().CountAsync(c => leaving.Contains(c.ContentType), ct);
            if (entries > 0)
            {
                return $"The tenant has {entries} {(entries == 1 ? "entry" : "entries")} of Content Types its current account owns. "
                    + "Moving it would leave them with no type to validate them. Remove them first.";
            }
        }

        var arriving = (await session.Query<AccountContentType>().Where(t => t.AccountId == to).ToListAsync(ct))
            .Select(t => t.Name).ToArray();
        if (arriving.Length > 0)
        {
            var clash = await tenantSession.Query<ContentTypeDefinition>().Where(d => arriving.Contains(d.Name)).Select(d => d.Name).FirstOrDefaultAsync(ct);
            if (clash is not null)
            {
                return $"The tenant has its own Content Type \"{clash}\", and the account it would join owns one with that name. Rename or remove it first.";
            }
        }

        return null;
    }

    /// <summary>The first of these tenants that has its own definition of this name, or null.</summary>
    public static async Task<string?> TenantWithOwnTypeAsync(
        IDocumentStore store, IEnumerable<string> tenantIds, string name, CancellationToken ct = default)
    {
        foreach (var id in tenantIds.Distinct(StringComparer.Ordinal))
        {
            await using var tenantSession = store.QuerySession(id);
            if (await tenantSession.Query<ContentTypeDefinition>().AnyAsync(d => d.Name == name, ct)) return id;
        }

        return null;
    }
}
