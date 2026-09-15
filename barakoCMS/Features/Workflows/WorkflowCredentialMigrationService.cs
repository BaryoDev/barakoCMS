using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Infrastructure.Security;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Features.Workflows;

/// <summary>
/// Encrypts, in place, the credential-named parameters of workflows stored before they were
/// encrypted on save (issue #765), and gives envelopes written before the version prefix existed
/// that prefix.
/// </summary>
/// <remarks>
/// A migration rather than "encrypt on next save", because there is no endpoint that saves an
/// existing workflow again: a definition is created and then only read, so waiting for a save would
/// leave every stored credential in clear for good.
///
/// It runs once at startup, over every partition that holds a workflow. It is safe to run on several
/// instances at once and on every boot, since <see cref="WebhookSigning.MigrateStoredCredentials"/>
/// leaves a prefixed value alone; two instances racing on one document each write a prefixed
/// envelope of the same plaintext. Runs already queued keep the parameters they copied, and the
/// runner still accepts those: an unprefixed envelope decrypts and a value in clear passes through.
/// </remarks>
internal sealed class WorkflowCredentialMigrationService : BackgroundService
{
    private const int BatchSize = 200;

    private readonly IDocumentStore _store;
    private readonly ISecretProtector _protector;
    private readonly ILogger<WorkflowCredentialMigrationService> _logger;

    public WorkflowCredentialMigrationService(
        IDocumentStore store, ISecretProtector protector, ILogger<WorkflowCredentialMigrationService> logger)
    {
        _store = store;
        _protector = protector;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await ProtectAllTenantsAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // Logged, not fatal. The runner still sends with a value in clear, so a failed pass
            // leaves things as they were before the upgrade, and the next boot tries again.
            _logger.LogError(ex, "Could not encrypt the credential parameters of stored workflows");
        }
    }

    public async Task<int> ProtectAllTenantsAsync(CancellationToken ct)
    {
        await _store.Storage.Database.EnsureStorageExistsAsync(typeof(WorkflowDefinition), ct);

        var changed = 0;
        foreach (var tenantId in await PartitionsAsync(ct))
        {
            await using var session = _store.LightweightSession(tenantId);
            changed += await ProtectStoredAsync(session, _protector, ct, _logger);
        }

        if (changed > 0)
        {
            _logger.LogInformation("Encrypted the credential parameters of {Count} stored workflow(s)", changed);
        }

        return changed;
    }

    private async Task<IReadOnlyList<string>> PartitionsAsync(CancellationToken ct)
    {
        var partitions = new List<string>();

        await using var conn = _store.Storage.Database.CreateConnection();
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"select distinct tenant_id from {_store.Options.DatabaseSchemaName}.mt_doc_workflowdefinition";

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            partitions.Add(reader.GetString(0));
        }

        return partitions;
    }

    /// <summary>Encrypts every stored workflow in one partition. Pure over the session, so a test drives it directly.</summary>
    /// <returns>The number of workflows that were rewritten.</returns>
    public static async Task<int> ProtectStoredAsync(
        IDocumentSession session, ISecretProtector protector, CancellationToken ct, ILogger? logger = null)
    {
        var changed = 0;

        for (var skip = 0; ; skip += BatchSize)
        {
            var batch = await session.Query<WorkflowDefinition>()
                .OrderBy(w => w.Id)
                .Skip(skip)
                .Take(BatchSize)
                .ToListAsync(ct);

            var dirty = 0;
            foreach (var workflow in batch)
            {
                var changedHere = WebhookSigning.MigrateStoredCredentials(workflow, protector, (index, name) =>
                    // The name and where it is, never the value: this is the log of a value that
                    // might be a credential.
                    logger?.LogWarning(
                        "The {Parameter} parameter of action {ActionIndex} on workflow {WorkflowId} could not be decrypted with the current key and was left as it is. Enter it again on the workflow.",
                        name, index, workflow.Id));

                if (!changedHere) continue;

                session.Store(workflow);
                dirty++;
            }

            if (dirty > 0)
            {
                await session.SaveChangesAsync(ct);
                changed += dirty;
            }

            if (batch.Count < BatchSize) break;
        }

        return changed;
    }
}
