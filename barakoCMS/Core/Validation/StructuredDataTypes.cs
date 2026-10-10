namespace barakoCMS.Core.Validation;

/// <summary>
/// The schema.org types a content type may declare in
/// <see cref="barakoCMS.Models.ContentTypeDefinition.StructuredDataType"/>, and the check a save runs.
/// </summary>
/// <remarks>
/// A short list on purpose. Each name here is one delivery knows how to fill from the field roles,
/// and a name it could not fill would emit a block search engines reject. Names are compared
/// exactly, in schema.org's own spelling.
/// </remarks>
internal static class StructuredDataTypes
{
    public const string Article = "Article";
    public const string NewsArticle = "NewsArticle";
    public const string BlogPosting = "BlogPosting";
    public const string Event = "Event";
    public const string Product = "Product";
    public const string WebPage = "WebPage";

    public static IReadOnlyList<string> Names { get; } =
        [Article, NewsArticle, BlogPosting, Event, Product, WebPage];

    /// <summary>The article family: a headline, a publish date and an author.</summary>
    public static bool IsArticle(string type) => type is Article or NewsArticle or BlogPosting;

    public static bool IsKnown(string? type) => type is not null && Names.Contains(type, StringComparer.Ordinal);

    /// <summary>What is wrong with the type a content type declares. Null is valid and means none.</summary>
    /// <remarks>The refused value is not repeated: it is not known to be a short word.</remarks>
    public static List<string> Errors(string? type)
    {
        var errors = new List<string>();

        if (type is not null && !IsKnown(type))
        {
            errors.Add($"structuredDataType must be one of {string.Join(", ", Names)}, spelled exactly. "
                + "Leave it out for a type that emits no structured data.");
        }

        return errors;
    }
}
