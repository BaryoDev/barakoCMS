using barakoCMS.Models;
using barakoCMS.Infrastructure.Audit;
using Marten;

namespace barakoCMS.Features.Accounts.ContentTypes;

internal static class AccountOwnedTypes
{
    /// <summary>
    /// The account a manager may change definitions of, or null with the answer already sent: 403
    /// for a caller who manages no account, 404 for an account they do not manage.
    /// </summary>
    public static async Task<TenantAccount?> ManagedAsync(
        HttpContext http, IQuerySession session, string slug, Func<Task> forbidden, Func<Task> notFound, CancellationToken ct)
    {
        var reach = await AccountReach.ForAsync(http, session, ct);
        if (reach is null) { await forbidden(); return null; }

        var account = await DefaultAccount.FindAsync(session, slug, ct);
        if (account is null || !reach.Covers(account.Id)) { await notFound(); return null; }

        return account;
    }
}

internal static class AccountTypeRoles
{
    /// <summary>
    /// Swaps each role name in the fields' VisibleToRoles for the id of the platform role of that
    /// name and returns a message for every entry that is not a platform role. A type shared by an
    /// account is read by tenants whose roles the manager's tenant does not hold, so a tenant role
    /// would name a different role, or none, for every tenant but one.
    /// </summary>
    public static async Task<List<string>> ToPlatformIdsAsync(
        IQuerySession session, IEnumerable<FieldDefinition> fields, CancellationToken ct)
    {
        var problems = new List<string>();
        var listed = fields.Where(f => f is { VisibleToRoles.Count: > 0 }).ToList();
        var entries = listed.SelectMany(f => f.VisibleToRoles).Where(e => !string.IsNullOrWhiteSpace(e)).Distinct(StringComparer.Ordinal).ToList();
        if (entries.Count == 0) return problems;

        var platform = await session.Query<Role>().Where(r => r.IsPlatform && r.DeletedAt == null).ToListAsync(ct);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var role = Guid.TryParse(entry, out var id)
                ? platform.FirstOrDefault(r => r.Id == id)
                : platform.FirstOrDefault(r => r.Name == entry);
            if (role is null)
                problems.Add($"\"{entry}\" is not a platform role. A content type owned by an account can only name platform roles in visibleToRoles.");
            else
                map[entry] = role.Id.ToString();
        }

        foreach (var field in listed)
            field.VisibleToRoles = field.VisibleToRoles.Select(e => map.TryGetValue(e, out var id) ? id : e).Distinct(StringComparer.Ordinal).ToList();

        return problems;
    }
}

internal static class AccountTypeAudit
{
    public static Task RecordAsync(
        IDocumentSession session, System.Security.Claims.ClaimsPrincipal actor, string tenantSlug, string action,
        TenantAccount account, string typeName, string? field, CancellationToken ct)
    {
        Guid.TryParse(actor.FindFirst("UserId")?.Value, out var actorId);
        var metadata = new Dictionary<string, object>
        {
            ["accountId"] = account.Id.ToString(),
            ["accountSlug"] = account.Slug,
        };
        if (field is not null) metadata["field"] = field;

        return AuditLog.RecordAsync(session, tenantSlug, action, actorId, actor.FindFirst("Username")?.Value,
            targetType: nameof(AccountContentType), targetId: typeName, metadata: metadata, ct: ct);
    }
}
