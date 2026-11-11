using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Features.Tenants;

/// <summary>What the tenant list says about a page of tenants beyond the tenant documents: how many members each has, and the mode of its site.</summary>
/// <remarks>
/// Each fact is one statement for the whole page, so the cost of a list does not grow with its page
/// size. The slugs come from a page the caller's account reach has already limited, and both
/// statements are filtered to exactly those slugs, so nothing here can count into a tenant the
/// caller did not reach.
///
/// The site mode is read from the stored entry on purpose and not from <c>PrivateSiteCache</c>,
/// which keeps only "not private" answers and may be stale.
///
/// With database tenancy enforced Postgres holds a session to its own tenant, so one statement
/// cannot read other tenants' site entries. There the mode is read with one session per tenant of
/// the page, as the other cross-tenant reads do.
/// </remarks>
internal sealed class TenantListFacts
{
    private readonly Dictionary<string, int> _members;
    private readonly Dictionary<string, string> _modes;

    private TenantListFacts(Dictionary<string, int> members, Dictionary<string, string> modes)
    {
        _members = members;
        _modes = modes;
    }

    /// <summary>Accounts holding a membership that is not Removed. Zero for a tenant nobody joined.</summary>
    public int MemberCount(string slug) => _members.GetValueOrDefault(slug);

    /// <summary>The <c>Mode</c> of the tenant's published site entry as stored, or null when it has none.</summary>
    public string? SiteMode(string slug) => _modes.GetValueOrDefault(slug);

    public static async Task<TenantListFacts> ForAsync(
        IQuerySession session, bool enforced, IReadOnlyCollection<string> slugs, CancellationToken ct)
    {
        if (slugs.Count == 0)
            return new TenantListFacts([], []);

        return new TenantListFacts(
            await MemberCountsAsync(session, slugs, ct),
            await SiteModesAsync(session, enforced, slugs, ct));
    }

    private static async Task<Dictionary<string, int>> MemberCountsAsync(
        IQuerySession session, IReadOnlyCollection<string> slugs, CancellationToken ct)
    {
        var schema = session.DocumentStore.Options.DatabaseSchemaName;
        var rows = await session.QueryAsync<CountRow>(
            "select jsonb_build_object('Slug', d.data ->> 'TenantSlug', 'Count', count(*)) "
            + $"from {schema}.mt_doc_memberships d "
            + $"where d.data ->> 'TenantSlug' in ({Placeholders(slugs.Count)}) "
            + $"and (d.data ->> 'Status')::integer <> {(int)MembershipStatus.Removed} "
            + "group by d.data ->> 'TenantSlug'",
            ct, [.. slugs]);

        return rows.ToDictionary(r => r.Slug, r => r.Count);
    }

    private static async Task<Dictionary<string, string>> SiteModesAsync(
        IQuerySession session, bool enforced, IReadOnlyCollection<string> slugs, CancellationToken ct)
    {
        if (!enforced)
            return await SiteModesAsync(session, [.. slugs.Select(TenantScopes.TenantIdFor)], ct);

        var modes = new Dictionary<string, string>();
        foreach (var slug in slugs)
        {
            await using var tenantSession = session.DocumentStore.QuerySession(TenantScopes.TenantIdFor(slug));
            foreach (var (found, mode) in await SiteModesAsync(tenantSession, [TenantScopes.TenantIdFor(slug)], ct))
                modes[found] = mode;
        }

        return modes;
    }

    /// <summary>
    /// One statement over the given Marten tenant ids. A tenant with several published site entries
    /// answers Private if any entry is, as <see cref="PrivateSite"/> does, and otherwise the newest.
    /// </summary>
    private static async Task<Dictionary<string, string>> SiteModesAsync(
        IQuerySession session, IReadOnlyCollection<string> tenantIds, CancellationToken ct)
    {
        var schema = session.DocumentStore.Options.DatabaseSchemaName;
        var rows = await session.QueryAsync<ModeRow>(
            "select jsonb_build_object('Slug', t.tenant_id, 'Mode', t.mode) from ("
            + "select distinct on (d.tenant_id) d.tenant_id, e.value as mode "
            + $"from {schema}.mt_doc_contents d cross join lateral jsonb_each_text(d.data -> 'Data') e "
            + $"where d.tenant_id in ({Placeholders(tenantIds.Count)}) "
            + $"and d.data ->> 'ContentType' = '{TenantProfiles.SiteType}' "
            + $"and (d.data ->> 'Status')::integer = {(int)ContentStatus.Published} "
            + "and lower(e.key) = 'mode' and e.value is not null "
            + "order by d.tenant_id, (lower(btrim(e.value)) = 'private') desc, d.data ->> 'CreatedAt' desc) t",
            ct, [.. tenantIds]);

        return rows.ToDictionary(r => TenantScopes.SlugFor(r.Slug), r => r.Mode);
    }

    private static string Placeholders(int count) => string.Join(", ", Enumerable.Repeat("?", count));

    internal sealed class CountRow
    {
        public string Slug { get; set; } = string.Empty;
        public int Count { get; set; }
    }

    internal sealed class ModeRow
    {
        public string Slug { get; set; } = string.Empty;
        public string Mode { get; set; } = string.Empty;
    }
}
