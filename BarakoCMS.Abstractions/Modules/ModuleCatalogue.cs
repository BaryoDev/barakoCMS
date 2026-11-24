using System.Globalization;
using System.Text.RegularExpressions;

namespace barakoCMS.Modules;

/// <summary>
/// Every module the host added or discovery found, and whether the enabled list let it run.
/// </summary>
/// <remarks>
/// <c>GET /api/modules</c> used to read the <see cref="IBarakoModule"/> singletons, which only ever
/// held the modules that ran. A module switched off by <c>BarakoCMS:Modules:Enabled</c> is still
/// present in the build, and an operator asking "is Files off, or not installed" needs the two
/// cases told apart. This records both.
///
/// Internal: it is how core answers one endpoint, not part of the module contract.
/// </remarks>
internal sealed partial class ModuleCatalogue
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

    /// <summary>
    /// Every settings section name the host serves itself, including <c>/api/tenant-settings/cdn-purge</c>.
    /// </summary>
    internal static readonly IReadOnlyList<string> ReservedSectionNames = [ReservedSectionName, "cdn-purge"];

    internal const int MaxCardsPerModule = 4;
    internal const int MaxCardTitleLength = 60;
    internal const int MaxCardRouteLength = 200;

    [GeneratedRegex(@"\A[a-z0-9][a-z0-9-]{0,39}\z", RegexOptions.NonBacktracking | RegexOptions.CultureInvariant)]
    private static partial Regex CardIdPattern();

    [GeneratedRegex(@"\A/api/[A-Za-z0-9_-]+(/[A-Za-z0-9_-]+)*\z", RegexOptions.NonBacktracking | RegexOptions.CultureInvariant)]
    private static partial Regex CardRoutePattern();

    [GeneratedRegex(@"\A/api/auth(/|\z)", RegexOptions.NonBacktracking | RegexOptions.CultureInvariant)]
    private static partial Regex AuthRoutePattern();

    private static bool IsUnsafeChar(char c) => char.IsControl(c)
        || char.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;

    public static ModuleCatalogue Of(
        IEnumerable<IBarakoModule> seen,
        IEnumerable<IBarakoModule> enabled,
        Action<string>? warn = null)
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
                Cards = running.Contains(m) ? ValidateCards(m, warn) : [],
            })
            .ToArray());
    }

    /// <summary>
    /// The cards that pass the rules on <see cref="ConsoleCard"/>. A broken card is dropped and
    /// reported by module name and card id, with control characters removed and the text cut, since
    /// the module wrote it.
    /// </summary>
    private static IReadOnlyList<ConsoleCard> ValidateCards(IBarakoModule module, Action<string>? warn)
    {
        var name = Clean(module.Name, 80);
        var kept = new List<ConsoleCard>();
        try
        {
            IReadOnlyList<ConsoleCard>? declared = module.ConsoleCards;
            if (declared is null)
            {
                return [];
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var card in declared)
            {
                if (card is null)
                {
                    continue;
                }

                var problem = Problem(card, ids);
                if (problem is null && kept.Count >= MaxCardsPerModule)
                {
                    problem = $"a module keeps at most {MaxCardsPerModule} cards";
                }

                if (problem is not null)
                {
                    warn?.Invoke($"Module '{name}' console card '{Clean(card.Id, 40)}' dropped: {problem}.");
                    continue;
                }

                ids.Add(card.Id);
                kept.Add(card with { Title = card.Title.Trim() });
            }
        }
        catch (Exception ex)
        {
            warn?.Invoke($"Module '{name}' console cards ignored: reading them threw {ex.GetType().Name}.");
            return [];
        }

        return kept.ToArray();
    }

    private static string? Problem(ConsoleCard card, HashSet<string> ids)
    {
        if (card.Id is null || !CardIdPattern().IsMatch(card.Id))
        {
            return "the id must be lowercase letters, digits and hyphens, 1 to 40 characters";
        }

        if (ids.Contains(card.Id))
        {
            return "the id is already used by another card in this module";
        }

        var title = card.Title?.Trim();
        if (string.IsNullOrEmpty(title) || title.Length > MaxCardTitleLength || title.Any(IsUnsafeChar))
        {
            return $"the title must be 1 to {MaxCardTitleLength} characters with no control or format characters";
        }

        if (card.Route is not null && AuthRoutePattern().IsMatch(card.Route))
        {
            return "the route must not be under /api/auth";
        }

        if (card.Route is null || card.Route.Length > MaxCardRouteLength || !CardRoutePattern().IsMatch(card.Route))
        {
            return $"the route must be a path under /api/ of at most {MaxCardRouteLength} characters, with no query, '..', '//', scheme, host or backslash";
        }

        return null;
    }

    private static string Clean(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        var cut = value.Length > max ? value[..max] : value;
        if (cut.Length > 0 && char.IsHighSurrogate(cut[^1]))
        {
            cut = cut[..^1];
        }

        return string.Concat(cut.Select(c => IsUnsafeChar(c) ? '?' : c));
    }

    // Only modules that run can serve a section, so a switched-off module does not stop the host.
    private static void CheckSectionNames(IEnumerable<IBarakoModule> running)
    {
        var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in running)
        {
            foreach (var section in module.SettingsSections)
            {
                if (ReservedSectionNames.Contains(section.Name, StringComparer.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Module '{module.Name}' declares a settings section named '{section.Name}'. "
                        + $"'{section.Name}' is reserved for the host's own route, so the section could never be reached.");
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

    /// <summary><see cref="IBarakoModule.ConsoleCards"/> after validation, empty when none pass.</summary>
    public IReadOnlyList<ConsoleCard> Cards { get; init; } = [];
}
