using Microsoft.Extensions.Configuration;

namespace barakoCMS.Modules;

/// <summary>
/// Which of the modules a host added or discovery found actually run, decided by configuration.
/// </summary>
/// <remarks>
/// <c>BarakoCMS:Modules:Enabled</c> is read as an array or as one comma-separated string, so both
/// <c>"Enabled": ["Accounting", "Files"]</c> in JSON and <c>BarakoCMS__Modules__Enabled=Accounting,Files</c>
/// in the environment work. Names match <see cref="IBarakoModule.Name"/> without regard to case.
///
/// Three states, and they are different on purpose:
/// <list type="bullet">
/// <item>Unset: every module runs, and the host logs one warning saying so. That is today's
/// behaviour, kept so an existing deployment does not lose its modules on upgrade.</item>
/// <item>Set to an empty string: core only.</item>
/// <item>Set to names: exactly those. A name that matches nothing refuses startup, because a typo
/// that silently leaves a module off is worse than a boot that says which names it knows.</item>
/// </list>
/// </remarks>
internal static class ModuleEnablement
{
    /// <summary>The enabled list.</summary>
    public const string EnabledKey = "BarakoCMS:Modules:Enabled";

    /// <summary>The same key as an environment variable, for messages.</summary>
    public const string EnabledEnvironmentVariable = "BarakoCMS__Modules__Enabled";

    /// <summary>
    /// Whether <c>AddBarakoCMS</c> scans the dependency context for modules. Defaults to on; a host
    /// can also turn it off in code with <see cref="BarakoModuleBuilder.Discover"/>.
    /// </summary>
    public const string DiscoverKey = "BarakoCMS:Modules:Discover";

    public const string UnsetWarning =
        "BarakoCMS:Modules:Enabled is not set, so every module found runs: {Modules}. Set it to the "
        + "names that should run, as an array or a comma-separated string (for example the "
        + "environment variable " + EnabledEnvironmentVariable + "=Accounting,Files), or to an empty "
        + "string for core only.";

    /// <summary>
    /// The names the configuration asks for. Null when the key is unset; empty when it is set to
    /// an empty string.
    /// </summary>
    public static IReadOnlyList<string>? ReadEnabled(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(EnabledKey);

        // A scalar, including the empty string. An environment variable or a single JSON string
        // arrives here.
        if (section.Value is { } value)
            return Split(value);

        // An array: the JSON provider flattens it to numbered children and gives the key itself
        // no value. Note an empty JSON array produces no children at all and so reads as unset;
        // use "" for core only.
        var children = section.GetChildren().ToList();
        if (children.Count == 0)
            return null;

        return children.SelectMany(child => Split(child.Value ?? string.Empty)).ToArray();
    }

    /// <summary>
    /// The modules from <paramref name="modules"/> that <paramref name="enabled"/> names, in the
    /// order they were given.
    /// </summary>
    /// <exception cref="InvalidOperationException">A name matches no module.</exception>
    public static IReadOnlyList<IBarakoModule> Apply(
        IReadOnlyList<IBarakoModule> modules, IReadOnlyList<string> enabled)
    {
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(enabled);

        var available = modules.Select(m => m.Name).ToList();

        var unknown = enabled
            .Where(name => !available.Contains(name, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (unknown.Count > 0)
        {
            var known = available.Count == 0
                ? "none: no module was added or discovered"
                : string.Join(", ", available.OrderBy(n => n, StringComparer.Ordinal));

            throw new InvalidOperationException(
                $"{EnabledKey} names {string.Join(", ", unknown.Select(n => $"'{n}'"))}, which "
                + $"match{(unknown.Count == 1 ? "es" : string.Empty)} no module. Available: {known}. "
                + "Refusing to start rather than run without a module the configuration asked for.");
        }

        return modules
            .Where(m => enabled.Contains(m.Name, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    private static string[] Split(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
