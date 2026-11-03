using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Security;
using barakoCMS.Models;
using barakoCMS.Modules;
using Marten;

namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// Finds a declared section and key, and resolves the effective value for a tenant.
/// </summary>
/// <remarks>
/// Only keys an enabled module declared resolve. Anything else answers null, so a typo cannot read
/// an arbitrary stored setting or configuration key.
/// </remarks>
internal sealed class TenantSettingsResolver(
    IQuerySession session,
    ISecretProtector protector,
    IConfiguration configuration,
    ModuleCatalogue catalogue,
    ILogger<TenantSettingsResolver> logger) : ITenantSettings
{
    public static ModuleSettingsSection? FindSection(ModuleCatalogue catalogue, string name) =>
        catalogue.Entries
            .Where(e => e.Enabled)
            .SelectMany(e => e.SettingsSections)
            .FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    public static ModuleSettingsKey? FindKey(ModuleSettingsSection section, string key) =>
        section.Keys.FirstOrDefault(k => string.Equals(k.Key, key, StringComparison.OrdinalIgnoreCase));

    public async Task<string?> GetOverrideAsync(string tenant, string section, string key, CancellationToken ct = default)
    {
        var declared = Declared(section, key);
        if (declared is null || !declared.Value.Key.TenantOverridable || IsDefault(tenant))
        {
            return null;
        }

        var row = await session.LoadAsync<TenantSettings>(TenantSettings.IdForTenant(tenant), ct);
        return OverrideOf(row, declared.Value.Section, declared.Value.Key);
    }

    public async Task<string?> GetAsync(string tenant, string section, string key, CancellationToken ct = default)
    {
        var declared = Declared(section, key);
        if (declared is null)
        {
            return null;
        }
        var (s, k) = declared.Value;

        if (k.TenantOverridable && !IsDefault(tenant))
        {
            var row = await session.LoadAsync<TenantSettings>(TenantSettings.IdForTenant(tenant), ct);
            if (OverrideOf(row, s, k) is { } own)
            {
                return own;
            }
        }

        if (!k.Secret)
        {
            var storedKey = $"{s.Name}:{k.Key}";
            var stored = await session.Query<SystemSetting>().FirstOrDefaultAsync(x => x.Key == storedKey, ct);
            if (!string.IsNullOrWhiteSpace(stored?.Value))
            {
                return stored.Value.Trim();
            }
        }

        if (!string.IsNullOrWhiteSpace(k.HostDefaultKey))
        {
            var configured = configuration[k.HostDefaultKey];
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return configured.Trim();
            }
        }

        return null;
    }

    private (ModuleSettingsSection Section, ModuleSettingsKey Key)? Declared(string section, string key)
    {
        var s = FindSection(catalogue, section);
        var k = s is null ? null : FindKey(s, key);
        return s is null || k is null ? null : (s, k);
    }

    private string? OverrideOf(TenantSettings? row, ModuleSettingsSection section, ModuleSettingsKey key)
    {
        if (row is null)
        {
            return null;
        }
        var id = TenantSettings.ValueKey(section.Name, key.Key);

        if (key.Secret)
        {
            if (!row.ProtectedValues.TryGetValue(id, out var sealedValue) || string.IsNullOrEmpty(sealedValue))
            {
                return null;
            }
            var plain = protector.Unprotect(sealedValue);
            if (string.IsNullOrWhiteSpace(plain))
            {
                logger.LogError(
                    "A tenant's stored secret for {Section} {Key} could not be decrypted. Secrets:Key or JWT:Key has "
                  + "changed since it was saved, and it has to be entered again.", section.Name, key.Key);
                return null;
            }

            return plain.Trim();
        }

        return row.Values.TryGetValue(id, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
    }

    private static bool IsDefault(string tenant) =>
        string.Equals(tenant, Tenant.DefaultSlug, StringComparison.OrdinalIgnoreCase);
}
