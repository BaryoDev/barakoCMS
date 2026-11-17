using System.Text.RegularExpressions;

namespace barakoCMS.Infrastructure;

internal static partial class Slugs
{
    /// <summary>
    /// The shape a slug has to have: lowercase letters, digits and hyphens, starting with a letter or
    /// digit, at most 63 characters. Applied to the route value too, so a name that cannot exist is a
    /// bad request rather than a 404, which is also how a removed route looks.
    /// </summary>
    internal static bool IsValid(string value) => SlugPattern().IsMatch(value);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,62}$")]
    private static partial Regex SlugPattern();
}
