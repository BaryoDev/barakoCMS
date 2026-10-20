using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Security;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// Resolves email credentials, preferring what an admin stored over what the deployment configured.
/// </summary>
/// <remarks>
/// Read on every send rather than cached. The point of the feature is that a change takes effect
/// without a restart, and a cache is a second thing that has to be told, in a process that may not
/// be the one the change was made in. Email sends are rare enough that one document read is not
/// worth the invalidation bug.
/// </remarks>
internal sealed class EmailSettingsProvider(
    IQuerySession session,
    ISecretProtector protector,
    IConfiguration config,
    ILogger<EmailSettingsProvider> logger) : IEmailSettingsProvider
{
    public Task<ResolvedEmailSettings> GetAsync(CancellationToken ct = default) =>
        ResolveAsync(null, ct);

    public Task<ResolvedEmailSettings> GetForTenantAsync(string tenantSlug, CancellationToken ct = default) =>
        ResolveAsync(string.Equals(tenantSlug, Tenant.DefaultSlug, StringComparison.OrdinalIgnoreCase) ? null : tenantSlug, ct);

    private async Task<ResolvedEmailSettings> ResolveAsync(string? tenantSlug, CancellationToken ct)
    {
        var configuredKey = Trimmed(config["Resend:ApiKey"]) ?? Trimmed(Environment.GetEnvironmentVariable("RESEND_API_KEY"));
        var configuredFrom = Trimmed(config["Resend:From"]);

        var host = await session.LoadAsync<EmailSettings>(EmailSettings.SingletonId, ct);
        var own = tenantSlug is null
            ? null
            : await session.LoadAsync<EmailSettings>(EmailSettings.IdForTenant(tenantSlug), ct);

        var apiKey = configuredKey;
        var apiKeySource = configuredKey is null ? EmailSettingSource.None : EmailSettingSource.Configuration;

        // The tenant's own key when it set one and it decrypts, else the host default stored in the
        // admin, else configuration.
        var ownKey = string.IsNullOrEmpty(own?.ProtectedApiKey) ? null : Trimmed(protector.Unprotect(own.ProtectedApiKey));
        var ownKeyInUse = ownKey is not null;

        if (ownKey is null && !string.IsNullOrEmpty(own?.ProtectedApiKey))
            logger.LogError(
                "A tenant's stored email API key could not be decrypted. Secrets:Key or JWT:Key has changed "
              + "since it was saved, and it has to be entered again. Falling back to the host's key and sender.");

        var storedKey = ownKey;
        if (storedKey is null && !string.IsNullOrEmpty(host?.ProtectedApiKey))
        {
            storedKey = Trimmed(protector.Unprotect(host.ProtectedApiKey));
            if (storedKey is null)
            {
                // A stored key that will not decrypt means the key material it was encrypted under
                // changed. Falling back to configuration silently would send the next invoice from
                // whatever the deployment was seeded with, which is not what the person who typed
                // this in asked for, so it is said out loud.
                logger.LogError(
                    "The stored email API key could not be decrypted. Secrets:Key or JWT:Key has changed "
                  + "since it was saved, and it has to be entered again. Falling back to configuration.");
            }
        }

        if (storedKey is not null)
        {
            apiKey = storedKey;
            apiKeySource = EmailSettingSource.Stored;
        }

        var from = configuredFrom;
        var fromSource = configuredFrom is null ? EmailSettingSource.None : EmailSettingSource.Configuration;

        // A tenant sender counts only with the tenant's own key, one that decrypted and is the key in use. Without one the message goes out on
        // the host's key, and a tenant-chosen From would let it send as any domain the host verified.
        var ownFrom = ownKeyInUse ? Trimmed(own!.FromAddress) : null;

        if ((ownFrom ?? Trimmed(host?.FromAddress)) is { } storedFrom)
        {
            from = storedFrom;
            fromSource = EmailSettingSource.Stored;
        }

        return new ResolvedEmailSettings(apiKey, from, apiKeySource, fromSource);
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
