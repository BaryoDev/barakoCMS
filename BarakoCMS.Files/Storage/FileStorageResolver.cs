using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Security;
using Marten;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace BarakoCMS.Files.Storage;

/// <summary>
/// Which storage a file is written to and read from, per request, so a tenant's saved setting
/// applies to the next upload without a restart.
/// </summary>
/// <remarks>
/// <para>
/// Writes go to the configured storage when the deployment sets any storage key in configuration,
/// which is how every deployment worked before tenants could choose. Otherwise they go where the
/// tenant's <see cref="StorageSettings"/> says, and to Postgres when it says nothing.
/// </para>
/// <para>
/// Reads follow the provider each file records, never the current setting, so files stored before
/// a switch keep downloading after it. A file in a bucket is read through the configured bucket when
/// there is one, else through the tenant's saved one. A file records that it is in a bucket, not
/// which, so the saved bucket cannot be pointed elsewhere while files are in it, and a bucket
/// configured later in the environment takes over reading every tenant's bucket files.
/// </para>
/// </remarks>
public sealed class FileStorageResolver(
    IFileStorage configured,
    PostgresFileStorage postgres,
    IEnumerable<IObjectStorageProvider> providers,
    IStorageEnvironment environment,
    IQuerySession session,
    IMemoryCache cache,
    ISecretProtector protector,
    TenantContext tenant,
    ILogger<FileStorageResolver> logger)
{
    private const string CachePrefix = "files:storage-settings:";

    /// <summary>Whether configuration decides storage, so the tenant's setting is not used.</summary>
    public bool ConfigurationDecides =>
        environment.Keys.Count > 0 || configured.Provider != StorageProviders.Postgres;

    /// <summary>The storage new files go to.</summary>
    public async Task<IFileStorage> ForNewFilesAsync(CancellationToken ct)
    {
        if (ConfigurationDecides)
        {
            return configured;
        }

        var settings = await SettingsAsync(ct);
        if (settings?.Provider != StorageProviders.S3)
        {
            return configured;
        }

        if (FromSettings(settings) is { } chosen)
        {
            return chosen;
        }

        // Uploads keep working, into Postgres, where they stay readable. GET reports the secret as
        // unreadable, which is the only way this is fixed: sending it again.
        logger.LogWarning(
            "Tenant {Tenant} chose S3 storage but its stored secret cannot be decrypted, so new files go to {Provider}",
            tenant.Slug, configured.Provider);
        return configured;
    }

    /// <summary>The storage holding this file's bytes.</summary>
    public async Task<IFileStorage> ForAsync(StoredFile file, CancellationToken ct)
    {
        if (string.Equals(file.Provider, configured.Provider, StringComparison.OrdinalIgnoreCase))
        {
            return configured;
        }

        if (string.Equals(file.Provider, StorageProviders.Postgres, StringComparison.OrdinalIgnoreCase))
        {
            return postgres;
        }

        return FromSettings(await SettingsAsync(ct), file.Provider) ?? configured;
    }

    /// <summary>The tenant's saved settings, cached until <see cref="Forget"/> is called for the tenant.</summary>
    public async Task<StorageSettings?> SettingsAsync(CancellationToken ct)
    {
        var key = CachePrefix + tenant.Slug;
        if (cache.TryGetValue(key, out StorageSettings? cached))
        {
            return cached;
        }

        var loaded = await session.LoadAsync<StorageSettings>(StorageSettings.SingletonId, ct);
        cache.Set(key, loaded, new MemoryCacheEntryOptions
        {
            // The shared cache has a size limit, so every entry has to say what it costs.
            Size = 1,
            // Saving evicts on the node that saved. Another node picks the change up within this.
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30),
        });
        return loaded;
    }

    /// <summary>Drops the cached settings for the current tenant, after a save.</summary>
    public void Forget() => cache.Remove(CachePrefix + tenant.Slug);

    public IObjectStorageProvider? Provider(string provider) =>
        providers.FirstOrDefault(p => string.Equals(p.Provider, provider, StringComparison.OrdinalIgnoreCase));

    private IFileStorage? FromSettings(StorageSettings? settings, string provider = StorageProviders.S3)
    {
        if (settings?.S3 is not { } s3 || Provider(provider) is not { } builder
            || string.IsNullOrEmpty(s3.ProtectedSecretKey)
            || protector.Unprotect(s3.ProtectedSecretKey) is not { Length: > 0 } secret)
        {
            return null;
        }

        return builder.Create(s3, secret);
    }
}
