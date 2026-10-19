using barakoCMS.Infrastructure.Auth.Mfa;
using barakoCMS.Infrastructure.Connectors;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using Marten;
using Marten.Patching;

namespace barakoCMS.Infrastructure.Security;

/// <summary>
/// At startup, moves every stored secret written under an older key to the current key of its
/// <see cref="KeyRing"/>, and reports how many no configured key opens.
/// </summary>
/// <remarks>
/// <para>
/// Covers the email API key, MFA secrets and connector credentials. Workflow credential parameters
/// are re-wrapped by <c>WorkflowCredentialMigrationService</c>, which already rewrites them at
/// startup, so one pass owns each document.
/// </para>
/// <para>
/// Each rewrite is a patch conditional on the stored value being the one that was read. A value an
/// operator changed in between is left to the newer write, and two instances running this at once
/// both try the same patch and only the first matches. The loser of either race keeps a value that
/// still opens under the older key, and the next start moves it.
/// </para>
/// <para>
/// The count is what makes a rotated key visible. Before the ring, a value that would not decrypt
/// was found at the moment it was needed, as a missing credential mid-send. It is logged by class
/// and count, never by value.
/// </para>
/// </remarks>
internal sealed class SecretRewrapService(
    IDocumentStore store,
    ISecretProtector secrets,
    IConnectorSecretProtector connectors,
    IMfaSecretProtector mfa,
    IConfiguration config,
    ILogger<SecretRewrapService> logger) : BackgroundService
{
    private const int BatchSize = 200;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RewrapAllAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // Logged, not fatal: every value still opens under the key that wrote it, so a failed
            // pass leaves things readable, and the next start tries again.
            logger.LogError(ex, "Could not re-wrap stored secrets under the current key");
        }
    }

    public async Task<SecretRewrapReport> RewrapAllAsync(CancellationToken ct)
    {
        var report = new SecretRewrapReport(
            await EmailAsync(ct),
            await MfaAsync(ct),
            await ConnectorsAsync(ct));

        Report("Secrets", "the email API key", RingOf(secrets), report.Email);
        Report("Mfa", "MFA secrets", RingOf(mfa), report.Mfa);
        Report("Connectors", "connector credentials", RingOf(connectors), report.Connectors);

        return report;
    }

    private async Task<RewrapCount> EmailAsync(CancellationToken ct)
    {
        if (RingOf(secrets) is not { } ring) return default;

        await store.Storage.Database.EnsureStorageExistsAsync(typeof(EmailSettings), ct);
        await using var session = store.LightweightSession();

        var rows = await session.Query<EmailSettings>()
            .Where(x => x.ProtectedApiKey != "")
            .ToListAsync(ct);

        var count = new RewrapCount();
        foreach (var stored in rows)
        {
            if (Rewrap(ring, stored.ProtectedApiKey, ref count) is not { } next) continue;

            var id = stored.Id;
            var old = stored.ProtectedApiKey;
            session.Patch<EmailSettings>(x => x.Id == id && x.ProtectedApiKey == old)
                .Set(x => x.ProtectedApiKey, next);
        }

        await session.SaveChangesAsync(ct);
        return count;
    }

    private async Task<RewrapCount> MfaAsync(CancellationToken ct)
    {
        if (RingOf(mfa) is not { } ring) return default;

        await store.Storage.Database.EnsureStorageExistsAsync(typeof(MfaSecret), ct);
        await using var session = store.LightweightSession();

        var count = new RewrapCount();
        var after = Guid.Empty;

        while (true)
        {
            var last = after;
            var batch = await session.Query<MfaSecret>()
                .Where(x => x.Id.CompareTo(last) > 0)
                .OrderBy(x => x.Id)
                .Take(BatchSize)
                .Select(x => new StoredValue(x.Id, x.EncryptedSecret))
                .ToListAsync(ct);

            foreach (var row in batch)
            {
                if (string.IsNullOrEmpty(row.Value)) continue;
                if (Rewrap(ring, row.Value, ref count) is not { } next) continue;

                var (id, old) = (row.Id, row.Value);
                session.Patch<MfaSecret>(x => x.Id == id && x.EncryptedSecret == old).Set(x => x.EncryptedSecret, next);
            }

            await session.SaveChangesAsync(ct);

            if (batch.Count < BatchSize) break;
            after = batch[^1].Id;
        }

        return count;
    }

    private async Task<RewrapCount> ConnectorsAsync(CancellationToken ct)
    {
        if (RingOf(connectors) is not { CanProtect: true } ring) return default;

        await store.Storage.Database.EnsureStorageExistsAsync(typeof(ConnectorSecret), ct);

        var partitions = await TenantPartitions.ListAsync(
            store,
            config,
            $"select distinct tenant_id from {store.Options.DatabaseSchemaName}.mt_doc_connector_secrets",
            ct);

        var count = new RewrapCount();

        foreach (var tenantId in partitions)
        {
            try
            {
                await using var session = store.LightweightSession(tenantId);
                var after = Guid.Empty;

                while (true)
                {
                    var last = after;
                    var batch = await session.Query<ConnectorSecret>()
                        .Where(x => x.Id.CompareTo(last) > 0)
                        .OrderBy(x => x.Id)
                        .Take(BatchSize)
                        .Select(x => new StoredValue(x.Id, x.ProtectedValue))
                        .ToListAsync(ct);

                    foreach (var row in batch)
                    {
                        if (string.IsNullOrEmpty(row.Value)) continue;
                        if (Rewrap(ring, row.Value, ref count) is not { } next) continue;

                        var (id, old) = (row.Id, row.Value);
                        session.Patch<ConnectorSecret>(x => x.Id == id && x.ProtectedValue == old)
                            .Set(x => x.ProtectedValue, next);
                    }

                    await session.SaveChangesAsync(ct);

                    if (batch.Count < BatchSize) break;
                    after = batch[^1].Id;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One partition must not hold up the rest, for the same reason the workflow pass
                // gives. With enforcement off the name comes from the rows, hence LogSafe.
                logger.LogError(
                    ex,
                    "Could not re-wrap the connector credentials of tenant {Tenant}; the pass continues with the next tenant",
                    barakoCMS.Infrastructure.Logging.LogSafe.Value(tenantId));
            }
        }

        return count;
    }

    /// <summary>The value to store instead, or null when it should stay as it is.</summary>
    private static string? Rewrap(KeyRing ring, string stored, ref RewrapCount count)
    {
        var plaintext = ring.Unprotect(stored);
        if (plaintext is null)
        {
            count = count with { Unopenable = count.Unopenable + 1 };
            return null;
        }

        if (ring.IsCurrent(stored)) return null;

        count = count with { Rewrapped = count.Rewrapped + 1 };
        return ring.Protect(plaintext);
    }

    private void Report(string section, string what, KeyRing? ring, RewrapCount count)
    {
        if (count.Rewrapped > 0)
        {
            logger.LogInformation(
                "Re-wrapped {Count} of {What} under {Section} key {KeyId}",
                count.Rewrapped, what, section, ring?.CurrentId);
        }

        if (count.Unopenable > 0)
        {
            logger.LogWarning(
                "{Count} of {What} do not decrypt under any configured {Section} key. Restore the key they were written under, as {Section}:Key or an entry under {Section}:Keys, or enter them again",
                count.Unopenable, what, section, section);
        }
    }

    private static KeyRing? RingOf(object protector) => (protector as IKeyRingProtector)?.Ring;

    private sealed record StoredValue(Guid Id, string Value);
}

internal readonly record struct RewrapCount(int Rewrapped, int Unopenable);

/// <summary>What one pass did, per class of stored secret.</summary>
internal sealed record SecretRewrapReport(RewrapCount Email, RewrapCount Mfa, RewrapCount Connectors);
