namespace barakoCMS.Modules;

/// <summary>One setting a module lets a tenant override (#896, #890).</summary>
/// <param name="Key">The key within the section, for example <c>From</c>. Matched without regard to case.</param>
/// <param name="Description">What the setting does, shown beside the field.</param>
/// <param name="Category">Groups keys on a settings screen, for example <c>Sender</c> or <c>Storage</c>.</param>
/// <param name="Secret">
/// A secret is stored protected and is write only: reads say whether it is set and never return it.
/// </param>
/// <param name="TenantOverridable">
/// False for a key that is shown with its description but that only the host sets. The tenant route
/// answers 400 for it.
/// </param>
/// <param name="HostDefaultKey">
/// The full configuration key that holds the host default, for example <c>Modules:Email.Smtp:From</c>.
/// Null when the host has no configured default.
/// </param>
public sealed record ModuleSettingsKey(
    string Key,
    string Description,
    string Category = "General",
    bool Secret = false,
    bool TenantOverridable = true,
    string? HostDefaultKey = null);

/// <summary>
/// A named group of settings a module declares. A tenant administrator may override its keys
/// through <c>PUT /api/tenant-settings/{section}</c>.
/// </summary>
/// <remarks>
/// An allowlist. A section or key the module did not declare cannot be read or written through that
/// route.
/// </remarks>
/// <param name="Name">The section name used in the route, for example <c>Email.Smtp</c>.</param>
/// <param name="Description">What the section configures.</param>
/// <param name="Keys">The declared keys.</param>
public sealed record ModuleSettingsSection(
    string Name,
    string Description,
    IReadOnlyList<ModuleSettingsKey> Keys);
