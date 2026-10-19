using barakoCMS.Modules;
using barakoCMS.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;

namespace barakoCMS.Features.Modules.Configuration;

/// <summary>
/// Reads one module's section and decides, per key, what may leave the server.
/// </summary>
/// <remarks>
/// Order of the checks, and why:
/// <list type="number">
/// <item>No declaration means no entries. The section is never walked, so an undeclared key cannot
/// reach the output by a bug further down.</item>
/// <item>A key the module did not list is reported as present and redacted. The module is the only
/// party that knows which keys are safe.</item>
/// <item>A listed key whose name reads as a credential is redacted anyway. A listing mistake should
/// not leak.</item>
/// <item>A listed key whose value reads as a credential is redacted anyway. A host that holds
/// <c>smtp://user:pass@host</c> is a password in a field named like something harmless.</item>
/// </list>
/// Nothing here logs.
/// </remarks>
internal sealed class ModuleConfigurationReader(IConfiguration configuration)
{
    public ModuleConfigurationResponse Read(ModuleCatalogueEntry module, bool includeDeployment)
    {
        var view = module.ConfigurationView;
        var section = string.IsNullOrWhiteSpace(view?.Section) ? $"Modules:{module.Name}" : view!.Section!;
        if (view is null || view.Keys.Count == 0)
        {
            return new ModuleConfigurationResponse(module.Name, section, []);
        }

        var declared = view.Keys
            .Where(k => !string.IsNullOrWhiteSpace(k.Key))
            .GroupBy(k => k.Key.Trim(':'), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First() with { Key = g.Key })
            .ToArray();

        var leaves = new List<(string Key, string Value)>();
        Collect(configuration.GetSection(section), string.Empty, leaves);

        var entries = new List<ModuleConfigurationEntry>();
        foreach (var (key, value) in leaves)
        {
            var match = declared.FirstOrDefault(d => Covers(d.Key, key));
            if (match is null)
            {
                // Not listed: only a deployment reader learns that it exists.
                if (includeDeployment)
                {
                    entries.Add(Entry(section, key, null, ConfigurationScope.Deployment));
                }
                continue;
            }

            if (match.Scope == ConfigurationScope.Deployment && !includeDeployment)
            {
                continue;
            }

            var show = !CredentialRedaction.IsCredentialName(key) && !CredentialRedaction.ValueHoldsCredential(value);
            entries.Add(Entry(section, key, show ? value : null, match.Scope));
        }

        foreach (var d in declared)
        {
            var scopeVisible = d.Scope == ConfigurationScope.Tenant || includeDeployment;
            if (scopeVisible && !leaves.Any(l => Covers(d.Key, l.Key)))
            {
                entries.Add(new ModuleConfigurationEntry(
                    d.Key, null, false, ScopeName(d.Scope), ConfigurationSources.Default));
            }
        }

        return new ModuleConfigurationResponse(
            module.Name, section, entries.OrderBy(e => e.Key, StringComparer.Ordinal).ToArray());
    }

    private ModuleConfigurationEntry Entry(string section, string key, string? shown, ConfigurationScope scope) =>
        new(key, shown, shown is null, ScopeName(scope), SourceOf($"{section}:{key}"));

    private static string ScopeName(ConfigurationScope scope) =>
        scope == ConfigurationScope.Tenant ? "tenant" : "deployment";

    private static bool Covers(string declared, string key) =>
        string.Equals(declared, key, StringComparison.OrdinalIgnoreCase)
        || key.StartsWith(declared + ":", StringComparison.OrdinalIgnoreCase);

    private static void Collect(IConfigurationSection node, string prefix, List<(string, string)> into)
    {
        foreach (var child in node.GetChildren())
        {
            var key = prefix.Length == 0 ? child.Key : $"{prefix}:{child.Key}";
            if (child.Value is not null)
            {
                into.Add((key, child.Value));
            }

            Collect(child, key, into);
        }
    }

    /// <summary>The last provider holding the key wins, as in the configuration root itself.</summary>
    private string SourceOf(string fullKey)
    {
        if (configuration is not IConfigurationRoot root)
        {
            return ConfigurationSources.Unknown;
        }

        foreach (var provider in root.Providers.Reverse())
        {
            if (!provider.TryGet(fullKey, out _))
            {
                continue;
            }

            return provider switch
            {
                EnvironmentVariablesConfigurationProvider => ConfigurationSources.Environment,
                JsonConfigurationProvider { Source.Path: { } path } when IsAppSettings(path) => ConfigurationSources.AppSettings,
                _ => ConfigurationSources.Unknown,
            };
        }

        return ConfigurationSources.Unknown;
    }

    private static bool IsAppSettings(string path)
    {
        var file = Path.GetFileName(path);
        return file.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase)
               && file.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    }
}
