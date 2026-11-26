using Microsoft.Extensions.Configuration;

namespace barakoCMS.Core.Validation;

/// <summary>
/// The opt-in rule that a new entry needs a defined content type. Off by default, so a type with no
/// definition keeps accepting entries as it always has.
/// </summary>
/// <remarks>
/// Read on use rather than at start, like <c>Content:Concurrency:Require</c>. Creating only: an
/// entry that already exists is never refused for the type it was stored under.
/// </remarks>
public static class RequiredContentType
{
    public const string Key = "Content:RequireContentType";

    public static bool IsOn(IConfiguration? configuration) => configuration?.GetValue(Key, false) ?? false;

    /// <summary>The longest name a message repeats back. A longer one is cut.</summary>
    public const int MaxShown = 64;

    public static string Refusal(string contentType) =>
        $"Content type '{Shown(contentType)}' has no definition, and this deployment only accepts entries of a defined type. "
      + "Define the type first.";

    public static string Ambiguous(string contentType) =>
        $"Content type '{Shown(contentType)}' matches more than one stored type that differ only by case. "
      + "Name it exactly as stored.";

    // The name is the caller's text and lands in a response or a workflow run's error, so control
    // characters are dropped and the length is capped.
    private static string Shown(string? name)
    {
        var clean = new string((name ?? string.Empty).Where(Kept).ToArray());
        if (clean.Length <= MaxShown)
            return clean;

        // A cut between the two halves of a surrogate pair leaves half a character, which is not valid text.
        var cut = char.IsHighSurrogate(clean[MaxShown - 1]) ? MaxShown - 1 : MaxShown;
        return clean[..cut] + "...";
    }

    // Control characters, line and paragraph separators, and format characters such as bidi overrides
    // and zero-width marks, which can make the message read other than it is.
    private static bool Kept(char c)
    {
        if (char.IsControl(c))
            return false;

        return System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) is not (
            System.Globalization.UnicodeCategory.Format
            or System.Globalization.UnicodeCategory.LineSeparator
            or System.Globalization.UnicodeCategory.ParagraphSeparator);
    }
}
