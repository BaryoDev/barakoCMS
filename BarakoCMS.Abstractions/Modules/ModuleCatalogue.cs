namespace barakoCMS.Modules;

/// <summary>
/// Every module the host added or discovery found, and whether the enabled list let it run.
/// </summary>
/// <remarks>
/// <c>GET /api/modules</c> used to read the <see cref="IBarakoModule"/> singletons, which only ever
/// held the modules that ran. A module switched off by <c>BarakoCMS:Modules:Enabled</c> is still
/// present in the build, and an operator asking "is Accounting off, or not installed" needs the two
/// cases told apart. This records both.
///
/// Internal: it is how core answers one endpoint, not part of the module contract.
/// </remarks>
internal sealed class ModuleCatalogue
{
    public ModuleCatalogue(IReadOnlyList<ModuleCatalogueEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        Entries = entries;
    }

    public IReadOnlyList<ModuleCatalogueEntry> Entries { get; }

    /// <summary>
    /// The settings section name the host serves itself at <c>/api/tenant-settings/email</c>. A module
    /// section with this name would never be reached, because the fixed route wins.
    /// </summary>
    internal const string ReservedSectionName = "email";

    public static ModuleCatalogue Of(IEnumerable<IBarakoModule> seen, IEnumerable<IBarakoModule> enabled)
    {
        var running = new HashSet<IBarakoModule>(enabled, ReferenceEqualityComparer.Instance);
        CheckSectionNames(running);
        return new ModuleCatalogue(seen
            .Select(m => new ModuleCatalogueEntry(m.Name, m.ContractVersion, running.Contains(m))
            {
                HttpContractVersion = m.HttpContractVersion,
                ConfigurationView = m.ConfigurationView,
                SettingsSections = m.SettingsSections,
                PackageId = m.PackageId,
                PackageVersion = m.PackageVersion,
                Category = ModuleCategory.Normalise(m.Category),
                Description = m.Description,
                Glyph = m.Glyph,
            })
            .ToArray());
    }

    // Only modules that run can serve a section, so a switched-off module does not stop the host.
    private static void CheckSectionNames(IEnumerable<IBarakoModule> running)
    {
        var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in running)
        {
            foreach (var section in module.SettingsSections)
            {
                if (string.Equals(section.Name, ReservedSectionName, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Module '{module.Name}' declares a settings section named '{section.Name}'. "
                        + $"'{ReservedSectionName}' is reserved for the host's own route, so the section could never be reached.");
                }

                if (!owners.TryAdd(section.Name, module.Name))
                {
                    throw new InvalidOperationException(
                        $"Modules '{owners[section.Name]}' and '{module.Name}' both declare a settings section named "
                        + $"'{section.Name}'. Section names are matched without regard to case and must be unique.");
                }
            }
        }
    }
}

/// <param name="Name"><see cref="IBarakoModule.Name"/>, verbatim.</param>
/// <param name="ContractVersion"><see cref="IBarakoModule.ContractVersion"/>, zero when unstated.</param>
/// <param name="Enabled">Whether the module was registered and runs in this process.</param>
internal sealed record ModuleCatalogueEntry(string Name, int ContractVersion, bool Enabled)
{
    /// <summary><see cref="IBarakoModule.HttpContractVersion"/>, zero when unstated.</summary>
    public int HttpContractVersion { get; init; }

    /// <summary><see cref="IBarakoModule.ConfigurationView"/>, null when the module declares nothing.</summary>
    public ModuleConfigurationView? ConfigurationView { get; init; }

    /// <summary><see cref="IBarakoModule.SettingsSections"/>, empty when the module declares nothing.</summary>
    public IReadOnlyList<ModuleSettingsSection> SettingsSections { get; init; } = [];

    /// <summary><see cref="IBarakoModule.PackageId"/>, null when unstated.</summary>
    public string? PackageId { get; init; }

    /// <summary><see cref="IBarakoModule.PackageVersion"/>, null when unstated.</summary>
    public string? PackageVersion { get; init; }

    /// <summary><see cref="IBarakoModule.Category"/> if it is in <see cref="ModuleCategory"/>, otherwise null.</summary>
    public string? Category { get; init; }

    /// <summary><see cref="IBarakoModule.Description"/>, null when unstated.</summary>
    public string? Description { get; init; }

    /// <summary><see cref="IBarakoModule.Glyph"/>, null when unstated.</summary>
    public string? Glyph { get; init; }
}
