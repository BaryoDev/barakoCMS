using barakoCMS.Features.Roles;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Features.ContentType.SetPublicDelivery;

/// <summary>Finds the roles that read only some rows of a content type.</summary>
/// <remarks>
/// Anonymous delivery serves every published entry. A role whose read rule tests a field of the
/// entry (an audience list, an owner id) says some entries are not for everyone, and the public
/// route has no role to test that against. So such a type is not switched on. A rule that tests
/// only the status is not a row limit: the public route serves published entries only.
/// </remarks>
internal static class RowLimitedReads
{
    public static async Task<List<string>> RolesOfAsync(
        IQuerySession session, string tenantSlug, string contentType, CancellationToken ct)
    {
        var roles = await RoleScope.In(session.Query<Role>(), tenantSlug).ToListAsync(ct);
        return roles
            .Where(r => r.Permissions.Any(p =>
                string.Equals(p.ContentTypeSlug, contentType, StringComparison.OrdinalIgnoreCase) && LimitsRows(p.Read)))
            .Select(r => r.Name)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private static bool LimitsRows(PermissionRule? read) =>
        read is { Enabled: true, Conditions: { Count: > 0 } conditions }
        && conditions.Keys.Any(k => !string.Equals(k, "$status", StringComparison.Ordinal));
}
