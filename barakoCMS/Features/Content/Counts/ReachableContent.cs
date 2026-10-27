using System.Security.Claims;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using Marten;
using Marten.Linq.MatchesSql;

namespace barakoCMS.Features.Content.Counts;

/// <summary>What a caller may read: queries to count over, and the types that could not be counted.</summary>
internal sealed record ContentReach(
    IReadOnlyList<IQueryable<barakoCMS.Models.Content>> Sources,
    IReadOnlyList<string> Uncounted);

/// <summary>
/// The entries a caller may read, as queries over the read predicate the list endpoint pages with and
/// the caller's branches. Shared by the status counts and the overview, so both count the same set.
/// </summary>
/// <remarks>
/// A type whose rules do not compile is named in <c>Uncounted</c> rather than loaded row by row.
/// Types read unconditionally share one source, so the number of queries does not grow with the
/// number of types.
/// </remarks>
internal static class ReachableContent
{
    internal static async Task<ContentReach> ForAsync(
        IQuerySession session,
        IPermissionResolver permissionResolver,
        IBranchScopeGuard branchGuard,
        ClaimsPrincipal principal,
        User user,
        string? contentType,
        CancellationToken ct)
    {
        IReadOnlyList<string> types = string.IsNullOrWhiteSpace(contentType)
            ? await session.Query<ContentTypeDefinition>()
                .Select(d => d.Name)
                .ToListAsync(ct)
            : [contentType];

        var unconditional = new List<string>();
        var conditional = new List<(string Type, ReadPredicate Predicate)>();
        var uncounted = new List<string>();

        foreach (var type in types)
        {
            var predicate = await permissionResolver.ReadPredicateAsync(user, type, ct);
            if (!predicate.Compiled)
            {
                // Named only to a caller with some read rule for it, so the list cannot tell anyone
                // which types exist that they may not read at all.
                if (await permissionResolver.CanPerformActionAsync(user, type, "read", null, ct))
                {
                    uncounted.Add(type);
                }
            }
            else if (predicate.Sql == ReadPredicate.All.Sql)
            {
                unconditional.Add(type);
            }
            else if (predicate.Sql != ReadPredicate.Nothing.Sql)
            {
                conditional.Add((type, predicate));
            }
        }

        // Counts what the caller may reach: a branch scoped type counts only the caller's branches.
        var branchScope = await branchGuard.FilterAsync(principal, ct);
        IQueryable<barakoCMS.Models.Content> Reachable(IQueryable<barakoCMS.Models.Content> rows) =>
            branchScope is null ? rows : rows.Where(c => c.MatchesSql(branchScope.Sql, branchScope.Parameters));

        var sources = new List<IQueryable<barakoCMS.Models.Content>>();
        if (unconditional.Count > 0)
        {
            sources.Add(Reachable(session.Query<barakoCMS.Models.Content>().Where(c => c.ContentType.IsOneOf(unconditional))));
        }

        foreach (var (type, predicate) in conditional)
        {
            sources.Add(Reachable(session.Query<barakoCMS.Models.Content>()
                .Where(c => c.ContentType == type)
                .Where(c => c.MatchesSql(predicate.Sql!, predicate.Parameters))));
        }

        return new ContentReach(sources, uncounted.Order(StringComparer.Ordinal).ToList());
    }
}
