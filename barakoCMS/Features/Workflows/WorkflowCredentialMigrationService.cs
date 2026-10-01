using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Security;
using barakoCMS.Models;
using Marten;
using LogSafe = barakoCMS.Infrastructure.Logging.LogSafe;

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
/// It runs once at startup, over every partition that holds a workflow, or over every registered
/// tenant and the default partition when Postgres enforces the tenant filter, where a workflow in a
/// partition with no <see cref="Tenant"/> document is not reached. That includes a single-tenant
/// deployment whose partition is a slug taken from its host name and never registered. The
/// application role cannot count what the policy hides from it, so with enforcement on the pass
/// always logs how many partitions and workflows it read: a pass that reached nothing must not look
/// like a pass with nothing left to do.
///
/// Only the credential-named parameters on a workflow's own actions are covered. The child actions
/// a Conditional action carries in its parameters are not parsed (issue #871).
///
/// It is safe to run on several instances at once and on every boot, since
/// <see cref="WebhookSigning.MigrateStoredCredentials"/> leaves a prefixed value alone; two
/// instances racing on one document each write a prefixed envelope of the same plaintext. Runs
/// already queued keep the parameters they copied, and the runner still accepts those: an
/// unprefixed envelope decrypts and a value in clear passes through.
/// </remarks>
internal sealed class WorkflowCredentialMigrationService : BackgroundService
{
    private const int BatchSize = 200;

    private readonly IDocumentStore _store;
    private readonly ISecretProtector _protector;
    private readonly IConfiguration _config;
    private readonly ILogger<WorkflowCredentialMigrationService> _logger;

    public WorkflowCredentialMigrationService(
        IDocumentStore store,
        ISecretProtector protector,
        IConfiguration config,
        ILogger<WorkflowCredentialMigrationService> logger)
    {
        _store = store;
        _protector = protector;
        _config = config;
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

        var partitions = await PartitionsAsync(ct);
        var visited = 0;
        var read = 0;
        var changed = 0;

        foreach (var tenantId in partitions)
        {
            try
            {
                await using var session = _store.LightweightSession(tenantId);
                var (readHere, changedHere) = await ProtectStoredAsync(session, _protector, ct, _logger);

                visited++;
                read += readHere;
                changed += changedHere;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One partition must not hold up the rest. The pass runs once per start and meets
                // the same row first every time, so stopping here would leave every later partition
                // as it was for good. With enforcement off the name comes from the rows, which a
                // request header can set, hence LogSafe.
                _logger.LogError(
                    ex,
                    "Could not encrypt the credential parameters of stored workflows in tenant {Tenant}; its workflows from the failing page on are left as they are, and the pass continues with the next tenant",
                    LogSafe.Value(tenantId));
            }
        }

        if (TenantPartitions.Enforced(_config))
        {
            _logger.LogInformation(
                "Read {Workflows} stored workflow(s) in {Visited} of {Partitions} partition(s) looking for credential parameters to encrypt",
                read, visited, partitions.Count);
        }

        if (changed > 0)
        {
            _logger.LogInformation("Encrypted the credential parameters of {Count} stored workflow(s)", changed);
        }

        return changed;
    }

    private Task<IReadOnlyList<string>> PartitionsAsync(CancellationToken ct) =>
        TenantPartitions.ListAsync(
            _store,
            _config,
            $"select distinct tenant_id from {_store.Options.DatabaseSchemaName}.mt_doc_workflowdefinition",
            ct);

    /// <summary>Encrypts every stored workflow in one partition. Pure over the session, so a test drives it directly.</summary>
    /// <returns>The number of workflows read, and how many of them were rewritten.</returns>
    public static async Task<(int Read, int Changed)> ProtectStoredAsync(
        IDocumentSession session, ISecretProtector protector, CancellationToken ct, ILogger? logger = null)
    {
        var read = 0;
        var changed = 0;

        for (var skip = 0; ; skip += BatchSize)
        {
            var batch = await session.Query<WorkflowDefinition>()
                .OrderBy(w => w.Id)
                .Skip(skip)
                .Take(BatchSize)
                .ToListAsync(ct);

            read += batch.Count;

            var dirty = 0;
            foreach (var workflow in batch)
            {
                var changedHere = WebhookSigning.MigrateStoredCredentials(workflow, protector, (index, name) =>
                    // The name and where it is, never the value: this is the log of a value that
                    // might be a credential.
                    logger?.LogWarning(
                        "The {Parameter} parameter of action {ActionIndex} on workflow {WorkflowId} could not be decrypted with the current key and was left as it is. Either Secrets:Key changed since it was saved, or it was saved in clear; restore the old key, or recreate the workflow.",
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

        return (read, changed);
    }
}
