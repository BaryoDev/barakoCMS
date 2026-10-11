using System.Globalization;
using System.Text.Json;
using barakoCMS.Core.Validation;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;

namespace barakoCMS.Features.Public;

/// <summary>
/// The schema.org JSON-LD block for one delivered entry, built from its type's field roles (#567).
/// </summary>
/// <remarks>
/// <para>
/// Read off the entry delivery is about to send, after <see cref="PublicDelivery.ToPublic"/>, the
/// file fields and the reference filter have run, never off the stored document. So a field that
/// is not Public, a token, a file that is not public and a reference to an entry delivery does not
/// serve are already gone, and cannot reach the block. This is not a second copy of the masking
/// rules: it has no rules of its own, only the roles.
/// </para>
/// <para>
/// A type that declares no <see cref="ContentTypeDefinition.StructuredDataType"/>, or one a save
/// would refuse, emits nothing. So does an entry with no title, because every type here needs a
/// name or a headline and a block without one is rejected by the consumers it is for.
/// </para>
/// <para>
/// Values are plain strings and one nested object, so the serializer writes valid JSON whatever
/// they hold. Escaping the block for an HTML <c>script</c> element is the renderer's job.
/// </para>
/// </remarks>
internal static class PublicStructuredData
{
    public const string Context = "https://schema.org";

    /// <summary>The entry with its block attached, or as it was when the type declares none.</summary>
    /// <param name="item">The entry as the response sends it.</param>
    /// <param name="definition">Its content type.</param>
    /// <param name="baseUrl">
    /// This deployment's absolute address, which a site-relative image such as
    /// <c>/api/public/files/{id}</c> is joined to. Null leaves such an image out.
    /// </param>
    public static PublicContentResponse Attach(
        PublicContentResponse item, ContentTypeDefinition definition, string? baseUrl = null) =>
        Build(item, definition, baseUrl) is { } block ? item with { StructuredData = block } : item;

    public static Dictionary<string, object>? Build(
        PublicContentResponse item, ContentTypeDefinition definition, string? baseUrl = null)
    {
        var type = definition.StructuredDataType;
        if (!StructuredDataTypes.IsKnown(type))
            return null;

        if (Text(item.Data, definition, FieldPresentation.TitleRole) is not { } title)
            return null;

        var article = StructuredDataTypes.IsArticle(type!);
        var block = new Dictionary<string, object>
        {
            ["@context"] = Context,
            ["@type"] = type!,
            [article ? "headline" : "name"] = title,
        };

        if (Text(item.Data, definition, FieldPresentation.SummaryRole) is { } summary)
            block["description"] = summary;

        if (type != StructuredDataTypes.Product
            && Date(item.Data, definition) is { } date)
        {
            block[type == StructuredDataTypes.Event ? "startDate" : "datePublished"] = date;
        }

        if (article || type == StructuredDataTypes.WebPage)
            block["dateModified"] = item.UpdatedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        if (Image(item.Data, definition, baseUrl) is { } image)
            block["image"] = image;

        if (article && Text(item.Data, definition, FieldPresentation.AuthorRole) is { } author)
            block["author"] = new Dictionary<string, object> { ["@type"] = "Person", ["name"] = author };

        if (item.Seo?.CanonicalUrl is { } canonical && IsWebAddress(canonical))
            block["url"] = canonical;

        return block;
    }

    private static object? Value(IReadOnlyDictionary<string, object> data, ContentTypeDefinition definition, string role)
    {
        if (FieldPresentation.FieldWithRole(definition, role) is not { } field)
            return null;

        foreach (var (key, value) in data)
        {
            if (string.Equals(key, field, StringComparison.OrdinalIgnoreCase))
                return value;
        }

        return null;
    }

    private static string? Text(IReadOnlyDictionary<string, object> data, ContentTypeDefinition definition, string role) =>
        Value(data, definition, role) switch
        {
            string s when !string.IsNullOrWhiteSpace(s) => s,
            JsonElement { ValueKind: JsonValueKind.String } je when !string.IsNullOrWhiteSpace(je.GetString()) => je.GetString(),
            _ => null,
        };

    // ISO 8601 as stored when it reads as a date, so a date field stays a date and a datetime keeps
    // its offset. Anything else is left out rather than sent as a date it is not.
    private static string? Date(IReadOnlyDictionary<string, object> data, ContentTypeDefinition definition) =>
        Value(data, definition, FieldPresentation.DateRole) switch
        {
            DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
            _ when Text(data, definition, FieldPresentation.DateRole) is { } text
                   && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _) => text,
            _ => null,
        };

    // A url field holds the address. A file field has already been replaced by the public file it
    // names, or left out when that file is not one a reader may fetch. A file kept in the database,
    // or on S3 with no public base URL, is served by this API at a site-relative path, so that path
    // is joined to the deployment's address. "//host/x" names another host, so it is not joined.
    private static string? Image(IReadOnlyDictionary<string, object> data, ContentTypeDefinition definition, string? baseUrl)
    {
        var url = Value(data, definition, FieldPresentation.ImageRole) switch
        {
            ResolvedFile file => file.Url,
            _ => Text(data, definition, FieldPresentation.ImageRole),
        };

        if (url is not null && baseUrl is not null && url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal))
            url = baseUrl.TrimEnd('/') + url;

        return url is not null && IsWebAddress(url) ? url : null;
    }

    private static bool IsWebAddress(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
}
