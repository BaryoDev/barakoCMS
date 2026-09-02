using barakoCMS.Models;

namespace barakoCMS.Features.Seo;

/// <summary>
/// The metadata a client site needs, resolved from an entry's fields with sensible fallbacks.
/// </summary>
/// <param name="Title">What a search result and a browser tab should say.</param>
/// <param name="Description">The snippet under the title.</param>
/// <param name="CanonicalUrl">The one URL this content should be indexed under, or null.</param>
/// <param name="ImageUrl">The social sharing image, or null.</param>
/// <param name="NoIndex">Whether search engines should be asked to skip this entry.</param>
internal sealed record SeoMetadata(
    string? Title,
    string? Description,
    string? CanonicalUrl,
    string? ImageUrl,
    bool NoIndex);

/// <summary>
/// The SEO field set a content type can opt into, and how an entry's values resolve.
/// </summary>
/// <remarks>
/// Ordinary fields on the content type, marked Public, rather than a separate structure. That is
/// what makes the delivery side almost free: they are validated, delivered, searched and scrubbed by
/// everything that already handles a field, and a type that does not want them simply does not have
/// them.
///
/// What is not free is agreeing on the names. Without that every agency invents its own convention
/// and every frontend re-implements the tags against a different one, which is the actual complaint
/// in the issue. These names are the contract.
/// </remarks>
internal static class SeoFields
{
    public const string MetaTitle = "MetaTitle";
    public const string MetaDescription = "MetaDescription";
    public const string CanonicalUrl = "CanonicalUrl";
    public const string SocialImage = "SocialImage";
    public const string NoIndex = "NoIndex";

    /// <summary>Where a search result usually stops showing a title.</summary>
    /// <remarks>
    /// Guidance rather than validation, and deliberately not enforced. Google truncates on pixel
    /// width rather than character count, so a hard limit here would be wrong in both directions:
    /// refusing a title that displays fine, and accepting one that does not. The admin shows the
    /// count and a preview; the decision stays with the person who can see the words.
    /// </remarks>
    public const int TitleGuidance = 60;

    /// <summary>Where a search result usually stops showing a description.</summary>
    public const int DescriptionGuidance = 155;

    /// <summary>The field set, in the order an editor should meet them.</summary>
    /// <remarks>
    /// All Public, because the whole point is that a frontend can read them anonymously. Marking any
    /// of them Sensitive would deliver an entry whose meta description silently disappeared for
    /// anonymous callers, which is every caller that matters for SEO.
    ///
    /// None are required. A type opting in should not make every existing entry invalid, and the
    /// fallbacks below are what make an empty value fine.
    /// </remarks>
    public static IReadOnlyList<FieldDefinition> Definitions() =>
    [
        new()
        {
            Name = MetaTitle,
            DisplayName = "Meta title",
            Type = "string",
            IsRequired = false,
            Sensitivity = SensitivityLevel.Public,
        },
        new()
        {
            Name = MetaDescription,
            DisplayName = "Meta description",
            Type = "text",
            IsRequired = false,
            Sensitivity = SensitivityLevel.Public,
        },
        new()
        {
            Name = CanonicalUrl,
            DisplayName = "Canonical URL",
            Type = "url",
            IsRequired = false,
            Sensitivity = SensitivityLevel.Public,
        },
        new()
        {
            Name = SocialImage,
            DisplayName = "Social sharing image",
            Type = "url",
            IsRequired = false,
            Sensitivity = SensitivityLevel.Public,
        },
        new()
        {
            Name = NoIndex,
            DisplayName = "Hide from search engines",
            Type = "bool",
            IsRequired = false,
            Sensitivity = SensitivityLevel.Public,
        },
    ];

    /// <summary>Whether a content type carries the SEO fields at all.</summary>
    public static bool IsOptedIn(ContentTypeDefinition definition) =>
        definition.Fields.Any(f => string.Equals(f.Name, MetaTitle, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The metadata for one entry, falling back rather than emitting empty tags.
    /// </summary>
    /// <remarks>
    /// The fallback is the part worth having. An empty meta title is worse than no tag at all: a
    /// search engine shown one indexes the page with nothing to display, whereas a page with no tag
    /// gets a title chosen from its content. So an unset meta title resolves to the entry's own
    /// title, found the way the admin finds it, and only a genuinely titleless entry resolves to
    /// null.
    ///
    /// Case-insensitive lookups throughout, because an entry can hold "metatitle" under a field
    /// declared "MetaTitle" and every other reader in this codebase matches that way.
    /// </remarks>
    public static SeoMetadata Resolve(IReadOnlyDictionary<string, object> data)
    {
        var title = Text(data, MetaTitle) ?? EntryTitle(data);

        return new SeoMetadata(
            Title: title,
            Description: Text(data, MetaDescription),
            CanonicalUrl: Text(data, CanonicalUrl),
            ImageUrl: Text(data, SocialImage),
            NoIndex: Flag(data, NoIndex));
    }

    /// <summary>
    /// The entry's own title, from the first of the names the admin looks for.
    /// </summary>
    /// <remarks>
    /// The same list and the same order the admin's own title resolution uses, deliberately. Two
    /// lists would answer differently the first time one of them gained a name, and the symptom
    /// would be a page whose tab and whose search result disagree.
    /// </remarks>
    private static string? EntryTitle(IReadOnlyDictionary<string, object> data)
    {
        foreach (var candidate in new[] { "Title", "Name", "DisplayName", "Label", "Subject", "Heading" })
        {
            if (Text(data, candidate) is { } value) return value;
        }

        return null;
    }

    private static string? Text(IReadOnlyDictionary<string, object> data, string field)
    {
        foreach (var (key, value) in data)
        {
            if (!string.Equals(key, field, StringComparison.OrdinalIgnoreCase)) continue;

            var text = value?.ToString();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        return null;
    }

    private static bool Flag(IReadOnlyDictionary<string, object> data, string field)
    {
        var text = Text(data, field);

        // "true" and "True" both, because the value arrives as a bool from an admin form and as its
        // JSON text after a round trip, and this must not answer differently depending on which.
        return text is not null && bool.TryParse(text, out var flag) && flag;
    }
}
