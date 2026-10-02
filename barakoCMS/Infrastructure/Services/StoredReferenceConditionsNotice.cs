using barakoCMS.Features.Roles;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using Marten;
using LogSafe = barakoCMS.Infrastructure.Logging.LogSafe;

namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// Logs, once per start, the roles that store a condition whose key holds a dot and follows no
/// reference in any tenant.
/// </summary>
/// <remarks>
/// Such a key used to be looked up in the entry's own data, and a role written then may have
/// matched rows that carried a key spelled that way. It is a reference path now, and one that does
/// not resolve denies, with nothing on any request to say which role stopped granting. An operator
/// reads here which roles to open.
///
/// A role is named when at least one of its conditions is one a role write would refuse in every
/// tenant: roles are stored once and content types per tenant, so a condition that resolves in one
/// tenant is working there.
/// </remarks>
internal sealed class StoredReferenceConditionsNotice(
    IDocumentStore store,
    IConfiguration config,
    ILogger<StoredReferenceConditionsNotice> logger) : BackgroundService
{
    private const int BatchSize = 200;

    private const int MaxNamesLogged = 50;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await ReportAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not list the roles that store a condition following no reference");
        }
    }

    public async Task<int> ReportAsync(CancellationToken ct)
    {
        await store.Storage.Database.EnsureStorageExistsAsync(typeof(Role), ct);
        await store.Storage.Database.EnsureStorageExistsAsync(typeof(ContentTypeDefinition), ct);

        var partitions = await TenantPartitions.ListAsync(
            store,
            config,
            $"select distinct tenant_id from {store.Options.DatabaseSchemaName}.mt_doc_contenttypedefinition",
            ct);

        var roles = await ReadAsync(store, partitions, ct, (tenantId, ex) => logger.LogError(
            ex,
            "Could not check the roles that store a condition following a reference in tenant {Tenant}",
            LogSafe.Value(tenantId)));

        if (roles.Count > 0)
        {
            logger.LogWarning(
                "{Count} role(s) store a permission condition whose key holds a dot and follows no reference in "
              + "any tenant, so the condition denies. Such a key was once matched against the entry's own data. "
              + "Open each role and correct or remove the condition: {Roles}",
                roles.Count, Listed(roles));
        }

        return roles.Count;
    }

    /// <summary>
    /// The roles holding at least one dotted condition that a role write would refuse in every one
    /// of these tenants. With no tenant to ask, every dotted condition counts.
    /// </summary>
    /// <param name="tenantFailed">
    /// Called for a tenant that could not be asked, which is then left out. Null lets the failure
    /// through.
    /// </param>
    public static async Task<List<(Guid Id, string Name)>> ReadAsync(
        IDocumentStore store,
        IReadOnlyList<string> tenantIds,
        CancellationToken ct,
        Action<string, Exception>? tenantFailed = null)
    {
        var holding = new List<Role>();

        await using (var session = store.QuerySession())
        {
            for (var skip = 0; ; skip += BatchSize)
            {
                var batch = await session.Query<Role>()
                    .OrderBy(r => r.Id)
                    .Skip(skip)
                    .Take(BatchSize)
                    .ToListAsync(ct);

                holding.AddRange(batch.Where(role => ReferenceConditionRules.HoldsPath(role.Permissions)));

                if (batch.Count < BatchSize)
                    break;
            }
        }

        if (holding.Count == 0)
            return [];

        var permissions = holding.SelectMany(role => role.Permissions ?? []).ToList();

        HashSet<ConditionIdentity>? nowhere = null;

        foreach (var tenantId in tenantIds)
        {
            try
            {
                await using var session = store.QuerySession(tenantId);
                var unresolved = await ReferenceConditionRules.UnresolvedAsync(session, permissions, ct);

                if (nowhere is null)
                    nowhere = unresolved;
                else
                    nowhere.IntersectWith(unresolved);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && tenantFailed is not null)
            {
                // The other tenants are still asked. A condition that follows a reference only in
                // the tenant that failed is named, which errs towards telling the operator.
                tenantFailed(tenantId, ex);
            }
        }

        // Tenants to ask and none answered: nothing is known, so nothing is named.
        if (tenantIds.Count > 0 && nowhere is null)
            return [];

        nowhere ??= ReferenceConditionRules.Paths(permissions).ToHashSet();

        return holding
            .Where(role => ReferenceConditionRules.Paths(role.Permissions).Any(nowhere.Contains))
            .Select(role => (role.Id, role.Name))
            .ToList();
    }

    private static string Listed(List<(Guid Id, string Name)> roles)
    {
        var shown = string.Join(", ", roles.Take(MaxNamesLogged).Select(role => $"{LogSafe.Value(role.Name)} ({role.Id})"));
        return roles.Count > MaxNamesLogged ? $"{shown} and {roles.Count - MaxNamesLogged} more" : shown;
    }
}
