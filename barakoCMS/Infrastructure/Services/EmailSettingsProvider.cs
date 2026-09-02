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
internal sealed class EmailSettingsProvider : IEmailSettingsProvider
{
    private readonly IQuerySession _session;
    private readonly ISecretProtector _protector;
    private readonly IConfiguration _config;
    private readonly ILogger<EmailSettingsProvider> _logger;

    public EmailSettingsProvider(
        IQuerySession session,
        ISecretProtector protector,
        IConfiguration config,
        ILogger<EmailSettingsProvider> logger)
    {
        _session = session;
        _protector = protector;
        _config = config;
        _logger = logger;
    }

    public async Task<ResolvedEmailSettings> GetAsync(CancellationToken ct = default)
    {
        var configuredKey = Trimmed(_config["Resend:ApiKey"]) ?? Trimmed(Environment.GetEnvironmentVariable("RESEND_API_KEY"));
        var configuredFrom = Trimmed(_config["Resend:From"]);

        var stored = await _session.LoadAsync<EmailSettings>(EmailSettings.SingletonId, ct);

        var apiKey = configuredKey;
        var apiKeySource = configuredKey is null ? EmailSettingSource.None : EmailSettingSource.Configuration;

        if (!string.IsNullOrEmpty(stored?.ProtectedApiKey))
        {
            var decrypted = Trimmed(_protector.Unprotect(stored.ProtectedApiKey));
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
                _logger.LogError(
                    "The stored email API key could not be decrypted. Secrets:Key or JWT:Key has changed "
                  + "since it was saved, and it has to be entered again. Falling back to configuration.");
            }
        }

        var from = configuredFrom;
        var fromSource = configuredFrom is null ? EmailSettingSource.None : EmailSettingSource.Configuration;

        if (Trimmed(stored?.FromAddress) is { } storedFrom)
        {
            from = storedFrom;
            fromSource = EmailSettingSource.Stored;
        }

        return new ResolvedEmailSettings(apiKey, from, apiKeySource, fromSource);
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
