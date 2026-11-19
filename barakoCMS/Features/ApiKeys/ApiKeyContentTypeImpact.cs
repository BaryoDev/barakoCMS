using barakoCMS.Models;
using Marten;

namespace barakoCMS.Features.ApiKeys;

internal sealed record ApiKeyWarning(Guid KeyId, string Name, string Prefix, List<string> ContentTypes);

/// <summary>How many live keys name a type across a set of tenants, and which of them sit in one of those tenants.</summary>
internal sealed record ApiKeyImpact(int Count, List<ApiKeyWarning> Keys);

/// <summary>
/// Which live keys (not revoked, not expired) of a tenant name a content type. A change that renames or removes the type
/// leaves those keys unable to push, so the response to that change lists them.
/// </summary>
internal static class ApiKeyContentTypeImpact
{
    /// <summary>The most keys one lookup reads, so a tenant set with a great many keys cannot make this unbounded.</summary>
    public const int MaxKeysRead = 10_000;

    public static async Task<List<ApiKeyWarning>> KeysNamingAsync(
        IQuerySession session, string tenant, IReadOnlyCollection<string> contentTypes, CancellationToken ct)
        => (await ImpactAsync(session, [tenant], tenant, contentTypes, ct)).Keys;

    /// <summary>
    /// One query over <paramref name="tenantSlugs"/>. <c>Count</c> covers every one of them; <c>Keys</c> is only
    /// the keys of <paramref name="listTenant"/>. A key is live when it is not revoked and its expiry is not before
    /// now, which is the same rule <c>ApiKeyAuthenticationHandler</c> refuses by.
    /// </summary>
    public static async Task<ApiKeyImpact> ImpactAsync(
        IQuerySession session, IReadOnlyCollection<string> tenantSlugs, string listTenant,
        IReadOnlyCollection<string> contentTypes, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var slugs = tenantSlugs.ToList();
        var keys = await session.Query<ApiKey>()
            .Where(k => slugs.Contains(k.TenantSlug) && !k.Revoked && (k.ExpiresAt == null || k.ExpiresAt >= now))
            .Take(MaxKeysRead)
            .ToListAsync(ct);

        var naming = keys
            .Select(k => (Key: k, Types: (k.ContentTypes ?? [])
                .Where(t => contentTypes.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList()))
            .Where(x => x.Types.Count > 0)
            .ToList();

        return new ApiKeyImpact(
            naming.Count,
            naming.Where(x => x.Key.TenantSlug == listTenant)
                .Select(x => new ApiKeyWarning(x.Key.Id, x.Key.Name, x.Key.Prefix, x.Types)).ToList());
    }
}
