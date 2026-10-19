namespace barakoCMS.Modules;

/// <summary>Who a configuration value belongs to (D26).</summary>
public enum ConfigurationScope
{
    /// <summary>One value for the whole deployment. Only a platform administrator may read it.</summary>
    Deployment = 0,

    /// <summary>A value a tenant's own administrator may read.</summary>
    Tenant = 1,
}

/// <summary>One configuration key a module says is safe to show, relative to its section.</summary>
/// <param name="Key">
/// The key under the section, for example <c>Host</c> or <c>Relay:Port</c>. Matched without regard to
/// case. A key also covers everything beneath it.
/// </param>
/// <param name="Scope">Defaults to <see cref="ConfigurationScope.Deployment"/>, the narrower reader.</param>
public sealed record ShowableConfigurationKey(string Key, ConfigurationScope Scope = ConfigurationScope.Deployment);

/// <summary>
/// What a module lets <c>GET /api/modules/{name}/configuration</c> show of its configuration.
/// </summary>
/// <remarks>
/// An allowlist. A key not named here is reported as present and redacted, never shown. A key named
/// here is still redacted when its name reads as a credential (<c>Password</c>, <c>ApiKey</c>,
/// <c>ConnectionString</c>) or its value holds one (<c>smtp://user:pass@host</c>, <c>password=...</c>),
/// so listing a key by mistake does not leak it.
/// </remarks>
/// <param name="Keys">The keys safe to show. Empty shows nothing.</param>
/// <param name="Section">
/// The configuration section the keys are under. Null means the module's own section,
/// <c>Modules:{Name}</c>.
/// </param>
public sealed record ModuleConfigurationView(
    IReadOnlyList<ShowableConfigurationKey> Keys,
    string? Section = null);
