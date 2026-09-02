namespace barakoCMS.Core.Interfaces;

/// <summary>
/// Where an email provider module gets its credentials, so a process owner can set them in the admin
/// instead of someone editing the deployment.
/// </summary>
/// <remarks>
/// This lives in the core because a module may depend on the core and never the other way round, and
/// the settings it resolves are stored by the core. A provider module asks this rather than reading
/// <c>IConfiguration</c> directly, which is what made email a deployment-time decision.
/// </remarks>
public interface IEmailSettingsProvider
{
    Task<ResolvedEmailSettings> GetAsync(CancellationToken ct = default);
}

/// <summary>Where each value came from as well as what it is.</summary>
/// <remarks>
/// The source travels with the value on purpose. With both a stored value and a configured one, an
/// operator sets one and watches the other win, and the only way to end that is for the screen to
/// say which is in force. Precedence is per field: a stored From address does not switch off a
/// configured API key, because the alternative is an all or nothing cliff where filling in one box
/// stops email working.
/// </remarks>
public sealed record ResolvedEmailSettings(
    string? ApiKey,
    string? FromAddress,
    EmailSettingSource ApiKeySource,
    EmailSettingSource FromAddressSource);

public enum EmailSettingSource
{
    /// <summary>Nothing is set anywhere.</summary>
    None,

    /// <summary>From the deployment: appsettings, an environment variable, whatever configures it.</summary>
    Configuration,

    /// <summary>Entered in the admin. Beats configuration, because a person set it most recently.</summary>
    Stored,
}
