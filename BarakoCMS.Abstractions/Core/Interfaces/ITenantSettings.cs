namespace barakoCMS.Core.Interfaces;

/// <summary>
/// The effective value of a setting a module declared in <c>IBarakoModule.SettingsSections</c> (#896).
/// </summary>
/// <remarks>
/// Order: the tenant's override, then the host default stored in the admin, then configuration.
/// Read on every call, not cached, so a change applies without a restart.
/// </remarks>
public interface ITenantSettings
{
    /// <summary>
    /// The value <paramref name="tenant"/> set for itself, or null when it set none, when the key is not
    /// declared, or when <paramref name="tenant"/> is the default tenant. The host default is not
    /// consulted, so a caller can tell an override from a fallback.
    /// </summary>
    Task<string?> GetOverrideAsync(string tenant, string section, string key, CancellationToken ct = default);

    /// <summary>The effective value for <paramref name="tenant"/>, or null when nothing sets one anywhere.</summary>
    Task<string?> GetAsync(string tenant, string section, string key, CancellationToken ct = default);
}
