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

    public static ModuleCatalogue Of(IEnumerable<IBarakoModule> seen, IEnumerable<IBarakoModule> enabled)
    {
        var running = new HashSet<IBarakoModule>(enabled, ReferenceEqualityComparer.Instance);
        return new ModuleCatalogue(seen
            .Select(m => new ModuleCatalogueEntry(m.Name, m.ContractVersion, running.Contains(m)))
            .ToArray());
    }
}

/// <param name="Name"><see cref="IBarakoModule.Name"/>, verbatim.</param>
/// <param name="ContractVersion"><see cref="IBarakoModule.ContractVersion"/>, zero when unstated.</param>
/// <param name="Enabled">Whether the module was registered and runs in this process.</param>
internal sealed record ModuleCatalogueEntry(string Name, int ContractVersion, bool Enabled);
