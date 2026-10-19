namespace barakoCMS.Features.Modules.Configuration;

internal sealed class ModuleConfigurationRequest
{
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// The configuration a module runs with. Only keys the module declared safe carry a value, and a
/// declared key is still redacted when its name or value reads as a credential.
/// </summary>
/// <param name="Module"><see cref="barakoCMS.Modules.IBarakoModule.Name"/>, verbatim.</param>
/// <param name="Section">The configuration section the keys are read from.</param>
/// <param name="Entries">Empty when the module declares nothing, never every key.</param>
internal sealed record ModuleConfigurationResponse(
    string Module,
    string Section,
    IReadOnlyList<ModuleConfigurationEntry> Entries);

/// <param name="Key">The key under the section, nested keys joined with a colon.</param>
/// <param name="Value">Null when <paramref name="Redacted"/>, and null when the key is unset.</param>
/// <param name="Redacted">True when a value exists and is withheld.</param>
/// <param name="Scope"><c>deployment</c> or <c>tenant</c>.</param>
/// <param name="Source">
/// <c>appsettings</c>, <c>environment</c>, <c>default</c> (no provider holds the key, so the code
/// default applies) or <c>unknown</c> (a provider this view cannot name).
/// </param>
internal sealed record ModuleConfigurationEntry(
    string Key,
    string? Value,
    bool Redacted,
    string Scope,
    string Source);

internal static class ConfigurationSources
{
    public const string AppSettings = "appsettings";
    public const string Environment = "environment";
    public const string Default = "default";
    public const string Unknown = "unknown";
}
