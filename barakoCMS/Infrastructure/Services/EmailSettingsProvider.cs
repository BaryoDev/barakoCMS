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
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant,
    ILogger<EmailSettingsProvider> logger) : IEmailSettingsProvider
{
    public async Task<ResolvedEmailSettings> GetAsync(CancellationToken ct = default)
    {
        var configuredKey = Trimmed(config["Resend:ApiKey"]) ?? Trimmed(Environment.GetEnvironmentVariable("RESEND_API_KEY"));
        var configuredFrom = Trimmed(config["Resend:From"]);

        var host = await session.LoadAsync<EmailSettings>(EmailSettings.SingletonId, ct);
        var own = tenant.IsDefault
            ? null
            : await session.LoadAsync<EmailSettings>(EmailSettings.IdForTenant(tenant.Slug), ct);

        var apiKey = configuredKey;
        var apiKeySource = configuredKey is null ? EmailSettingSource.None : EmailSettingSource.Configuration;

        // Per field: the tenant's own value when it set one, else the host default stored in the
        // admin, else configuration. A tenant that sets only a sender keeps the host's key.
        var protectedKey = !string.IsNullOrEmpty(own?.ProtectedApiKey) ? own.ProtectedApiKey : host?.ProtectedApiKey;

        if (!string.IsNullOrEmpty(protectedKey))
        {
            var decrypted = Trimmed(protector.Unprotect(protectedKey));
            if (decrypted is not null)
            {
                apiKey = decrypted;
                apiKeySource = EmailSettingSource.Stored;
            }
            else
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

        var from = configuredFrom;
        var fromSource = configuredFrom is null ? EmailSettingSource.None : EmailSettingSource.Configuration;

        if ((Trimmed(own?.FromAddress) ?? Trimmed(host?.FromAddress)) is { } storedFrom)
        {
            from = storedFrom;
            fromSource = EmailSettingSource.Stored;
        }

        return new ResolvedEmailSettings(apiKey, from, apiKeySource, fromSource);
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
