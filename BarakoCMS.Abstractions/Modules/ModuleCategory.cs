namespace barakoCMS.Modules;

/// <summary>
/// The closed set of groups <see cref="IBarakoModule.Category"/> may name. Growing it is a
/// deliberate addition for every console, not something a module does on its own.
/// </summary>
public static class ModuleCategory
{
    public const string Content = "Content";
    public const string Comms = "Comms";
    public const string Access = "Access";
    public const string Platform = "Platform";
    public const string Insight = "Insight";
    public const string Business = "Business";

    public static IReadOnlyList<string> All { get; } = [Content, Comms, Access, Platform, Insight, Business];

    /// <summary>The matching name from the set, or null when <paramref name="value"/> is not in it.</summary>
    public static string? Normalise(string? value)
        => All.FirstOrDefault(c => string.Equals(c, value, StringComparison.Ordinal));
}
