using barakoCMS.Models;
using Marten;

namespace barakoCMS.Features.Roles;

/// <summary>How many people hold each role of a page, in one statement however long the page is.</summary>
/// <remarks>
/// A person is counted once per role however they came to hold it. What counts follows
/// <c>MembershipRoles</c>:
/// <list type="bullet">
/// <item>A tenant's own role is held through an Active membership in the tenant that owns it, and
/// nowhere else. Another tenant's members are never counted, and a role placed on a user or a group
/// grants nothing for a tenant role, so it is not counted either.</item>
/// <item>A platform role is held through an Active membership in the caller's tenant, through the
/// user's own roles (a platform role is global, so such a user holds it in every tenant), or
/// through a live group of the caller's tenant, whether the group lists the user or the user names
/// the group.</item>
/// </list>
/// </remarks>
internal static class RoleHolderCounts
{
    public static async Task<Dictionary<Guid, int>> ForAsync(
        IQuerySession session, string tenantSlug, IReadOnlyCollection<Role> roles, CancellationToken ct)
    {
        if (roles.Count == 0)
            return [];

        var schema = session.DocumentStore.Options.DatabaseSchemaName;
        var parameters = new List<object>();

        var scope = string.Join(", ", roles.Select(r =>
        {
            parameters.Add(r.Id.ToString());
            parameters.Add(RoleScope.OwnerOf(r) ?? tenantSlug);
            return "(?, ?)";
        }));

        var sql = new System.Text.StringBuilder()
            .Append("select jsonb_build_object('RoleId', h.role_id, 'Count', count(distinct h.user_id)) from (")
            .Append("select r.role_id as role_id, m.data ->> 'UserId' as user_id ")
            .Append($"from {schema}.mt_doc_memberships m ")
            .Append($"cross join lateral jsonb_array_elements_text({Array("m.data -> 'RoleIds'")}) r(role_id) ")
            .Append($"join (values {scope}) p(role_id, slug) on p.role_id = r.role_id and p.slug = m.data ->> 'TenantSlug' ")
            .Append($"where (m.data ->> 'Status')::integer = {(int)MembershipStatus.Active}");

        var platform = roles.Where(r => r.IsPlatform).Select(r => r.Id.ToString()).ToList();
        if (platform.Count > 0)
        {
            var ids = Placeholders(platform.Count);
            sql.Append(" union all ")
                .Append("select r.role_id, u.id::text ")
                .Append($"from {schema}.mt_doc_users u ")
                .Append($"cross join lateral jsonb_array_elements_text({Array("u.data -> 'RoleIds'")}) r(role_id) ")
                .Append($"where r.role_id in ({ids})");
            parameters.AddRange(platform);

            sql.Append(" union all ")
                .Append("select r.role_id, x.user_id ")
                .Append($"from {schema}.mt_doc_usergroup g ")
                .Append($"cross join lateral jsonb_array_elements_text({Array("g.data -> 'RoleIds'")}) r(role_id) ")
                .Append("cross join lateral (")
                .Append($"select jsonb_array_elements_text({Array("g.data -> 'UserIds'")}) as user_id ")
                .Append("union ")
                .Append($"select gu.id::text from {schema}.mt_doc_users gu where gu.data -> 'GroupIds' @> jsonb_build_array(g.id::text)")
                .Append(") x ")
                .Append($"where g.tenant_id = ? and g.data ->> 'DeletedAt' is null and r.role_id in ({ids})");
            parameters.Add(session.TenantId);
            parameters.AddRange(platform);
        }

        sql.Append(") h group by h.role_id");

        var rows = await session.QueryAsync<CountRow>(sql.ToString(), ct, [.. parameters]);
        return rows.ToDictionary(r => Guid.Parse(r.RoleId), r => r.Count);
    }

    /// <summary>The array at a path, or an empty one when a stored document has null or nothing there.</summary>
    private static string Array(string path) =>
        $"case when jsonb_typeof({path}) = 'array' then {path} else '[]'::jsonb end";

    private static string Placeholders(int count) => string.Join(", ", Enumerable.Repeat("?", count));

    internal sealed class CountRow
    {
        public string RoleId { get; set; } = string.Empty;
        public int Count { get; set; }
    }
}
