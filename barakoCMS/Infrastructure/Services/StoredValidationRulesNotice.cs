using barakoCMS.Core.Validation;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using Marten;
using LogSafe = barakoCMS.Infrastructure.Logging.LogSafe;

namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// Logs, once per start, the content types that store validation rules.
/// </summary>
/// <remarks>
/// Rules were stored and ignored before they were enforced, so a type written back then may carry a
/// rule nobody has seen applied. An operator reads here which types now refuse entries, and which
/// carry a rule that a save would refuse today and that is therefore skipped.
/// </remarks>
internal sealed class StoredValidationRulesNotice(
    IDocumentStore store,
    IConfiguration config,
    ILogger<StoredValidationRulesNotice> logger) : BackgroundService
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
            logger.LogError(ex, "Could not list the content types that store validation rules");
        }
    }

    public async Task<int> ReportAsync(CancellationToken ct)
    {
        await store.Storage.Database.EnsureStorageExistsAsync(typeof(ContentTypeDefinition), ct);

        var partitions = await TenantPartitions.ListAsync(
            store,
            config,
            $"select distinct tenant_id from {store.Options.DatabaseSchemaName}.mt_doc_contenttypedefinition",
            ct);

        var found = 0;

        foreach (var tenantId in partitions)
        {
            try
            {
                await using var session = store.LightweightSession(tenantId);
                var (applied, skipped) = await ReadAsync(session, ct);
                found += applied.Count + skipped.Count;

                if (applied.Count > 0)
                {
                    logger.LogWarning(
                        "Tenant {Tenant}: {Count} content type(s) store validation rules, and entry writes that break them are now refused: {Types}",
                        LogSafe.Value(tenantId), applied.Count, Listed(applied));
                }

                if (skipped.Count > 0)
                {
                    logger.LogWarning(
                        "Tenant {Tenant}: {Count} content type(s) store a validation rule that is not applied, because saving it today would be refused: {Types}",
                        LogSafe.Value(tenantId), skipped.Count, Listed(skipped));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(
                    ex,
                    "Could not list the content types that store validation rules in tenant {Tenant}",
                    LogSafe.Value(tenantId));
            }
        }

        return found;
    }

    public static async Task<(List<string> Applied, List<string> Skipped)> ReadAsync(
        IQuerySession session, CancellationToken ct)
    {
        var applied = new List<string>();
        var skipped = new List<string>();

        for (var skip = 0; ; skip += BatchSize)
        {
            var batch = await session.Query<ContentTypeDefinition>()
                .OrderBy(d => d.Id)
                .Skip(skip)
                .Take(BatchSize)
                .ToListAsync(ct);

            foreach (var definition in batch.Where(FieldRules.HasRules))
            {
                var refused = definition.Fields.Any(f => f is not null && FieldRules.DefinitionErrors(f).Count > 0);
                (refused ? skipped : applied).Add(definition.Name);
            }

            if (batch.Count < BatchSize)
                break;
        }

        return (applied, skipped);
    }

    private static string Listed(List<string> names)
    {
        var shown = string.Join(", ", names.Take(MaxNamesLogged).Select(name => LogSafe.Value(name)));
        return names.Count > MaxNamesLogged ? $"{shown} and {names.Count - MaxNamesLogged} more" : shown;
    }
}
