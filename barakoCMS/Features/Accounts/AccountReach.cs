using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Features.Accounts;

/// <summary>
/// Which tenants and accounts a signed-in caller may manage: all of them, or only the accounts they
/// have been made a manager of.
/// </summary>
/// <remarks>
/// A caller holding <c>manage_tenants</c> (or the SuperAdmin fallback) through their roles reaches
/// everything, exactly as before accounts existed. Anyone else reaches the tenants of the accounts
/// they hold an <see cref="TenantAccountManager"/> grant for, and nothing outside them. The default
/// account, <see cref="TenantAccount.DefaultId"/>, is an account like any other here.
/// </remarks>
internal sealed class AccountReach
{
    /// <summary>The gate the tenant endpoints have always asked for, now the global half of the check.</summary>
    internal static readonly RequiredCapability Global = new(SystemCapabilities.ManageTenants, ["SuperAdmin"]);

    private AccountReach(bool isGlobal, IReadOnlyList<Guid> accountIds)
    {
        IsGlobal = isGlobal;
        AccountIds = accountIds;
    }

    public bool IsGlobal { get; }

    /// <summary>The accounts a scoped caller manages. Empty for a global caller, who needs no list.</summary>
    public IReadOnlyList<Guid> AccountIds { get; }

    public bool Covers(Guid accountId) => IsGlobal || AccountIds.Contains(accountId);

    public bool Covers(Tenant tenant) => Covers(tenant.AccountId ?? TenantAccount.DefaultId);

    /// <summary>The caller's reach, or null when they may manage no tenant at all.</summary>
    public static async Task<AccountReach?> ForAsync(HttpContext http, IQuerySession session, CancellationToken ct)
    {
        if (await CapabilityGateProcessor.HoldsAsync(http, Global, ct))
            return new AccountReach(true, []);

        if (!Guid.TryParse(http.User.FindFirst("UserId")?.Value, out var userId))
            return null;

        var ids = await session.Query<TenantAccountManager>()
            .Where(m => m.UserId == userId)
            .Select(m => m.AccountId)
            .ToListAsync(ct);

        return ids.Count == 0 ? null : new AccountReach(false, ids.Distinct().ToList());
    }

    /// <summary>The tenants this caller reaches, as a query that can be filtered and paged further.</summary>
    public IQueryable<Tenant> Tenants(IQuerySession session)
    {
        var all = session.Query<Tenant>();
        if (IsGlobal)
            return all;

        var ids = AccountIds.Select(id => (Guid?)id).ToList();
        return AccountIds.Contains(TenantAccount.DefaultId)
            ? all.Where(t => t.AccountId == null || ids.Contains(t.AccountId))
            : all.Where(t => ids.Contains(t.AccountId));
    }

    /// <summary>Stores null for the default account, so a tenant on it is stored as it always was.</summary>
    public static Guid? Stored(Guid accountId) => accountId == TenantAccount.DefaultId ? null : accountId;
}
