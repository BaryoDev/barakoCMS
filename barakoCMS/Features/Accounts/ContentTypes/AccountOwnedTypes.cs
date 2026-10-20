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
