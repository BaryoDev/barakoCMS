using System.Globalization;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace barakoCMS.Infrastructure.Sync;

/// <summary>The items one response held, or why none could be read from it.</summary>
/// <param name="Rows">
/// One dictionary per item, keyed by the path a <c>CollectionSync.FieldMap</c> names. Every value is
/// a string: what the mapped content field's declared type means is decided later, against the
/// schema, so this layer has no opinion about whether "1200" is a number.
/// </param>
/// <param name="Error">Why nothing could be read, or null. Never the body itself.</param>
internal sealed record SyncPayload(IReadOnlyList<IReadOnlyDictionary<string, string>> Rows, string? Error)
{
    public static SyncPayload Failed(string error) => new([], error);

    public bool Ok => Error is null;

    /// <summary>The source held more items than were read, so the rows are not all of it.</summary>
    public bool Truncated { get; init; }
}

/// <summary>
/// Turns a JSON or feed response into flat rows a field mapping can read.
/// </summary>
/// <remarks>
/// Static and pure, taking the body as a string, so the shape of a source can be tested without a
/// network, a database or a host. That matters more here than usual: the cases that break a sync are
/// a NuGet response with a different envelope, an Atom feed where RSS was expected and a feed with a
/// DOCTYPE in it, and none of those needs a running provider to reproduce.
/// </remarks>
internal static class SyncPayloadReader
{
    /// <summary>How deep into a nested item the flattener goes.</summary>
    /// <remarks>
    /// A field mapping addresses a value an operator can see in a sample response. Five levels is
    /// past anything they would write by hand, and the limit is what stops a deeply nested or
    /// self-similar document turning one response into a very large dictionary.
    /// </remarks>
    private const int MaxDepth = 5;

    /// <summary>How many paths one item contributes before the rest are ignored.</summary>
    /// <remarks>
    /// Counted over the paths a mapping names when the caller says which those are. Counted over
    /// every path, a real GitHub search result for one issue flattens to about 200, so one more
    /// label pushed <c>state_reason</c> past the cap.
    /// </remarks>
    private const int MaxPathsPerItem = 200;

    /// <summary>Reads the items out of a JSON document.</summary>
    /// <param name="body">The response, as the provider sent it.</param>
    /// <param name="itemsPath">
    /// A dotted path to the array, or empty when the document is itself the array.
    /// </param>
    /// <param name="maxItems">How many items are read before the rest are ignored.</param>
    /// <param name="paths">
    /// The paths a mapping reads, or null for every path. A path keeps the values beneath it too, so
    /// <c>assignees</c> keeps <c>assignees[0].login</c>, and an index matches any index, so
    /// <c>labels[].name</c> and <c>labels[0].name</c> both keep every label's name.
    /// </param>
    public static SyncPayload ReadJson(
        string body, string itemsPath, int maxItems, IReadOnlyCollection<string>? paths = null)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            // The parser's message, which says where it gave up, and never the body. An operator
            // debugging a mapping needs the position; nobody needs the provider's payload in a
            // stored error string.
            return SyncPayload.Failed($"The response is not valid JSON: {ex.Message}");
        }

        using (document)
        {
            var element = document.RootElement;

            foreach (var segment in Segments(itemsPath))
            {
                if (element.ValueKind != JsonValueKind.Object
                    || !element.TryGetProperty(segment, out var next))
                {
                    return SyncPayload.Failed(
                        $"The response has no '{itemsPath}' to read items from.");
                }

                element = next;
            }

            if (element.ValueKind != JsonValueKind.Array)
            {
                return SyncPayload.Failed(itemsPath.Length == 0
                    ? "The response is not a JSON array, so ItemsPath has to name the array within it."
                    : $"'{itemsPath}' is not a JSON array.");
            }

            var rows = new List<IReadOnlyDictionary<string, string>>();
            var wanted = paths?.Select(Unindexed).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var item in element.EnumerateArray())
            {
                if (rows.Count >= maxItems) break;

                var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                Flatten(item, prefix: string.Empty, depth: 0, row, wanted);
                rows.Add(row);
            }

            return new SyncPayload(rows, null) { Truncated = element.GetArrayLength() > rows.Count };
        }
    }

    private static IEnumerable<string> Segments(string path) =>
        path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Unindexed(string path) =>
        System.Text.RegularExpressions.Regex.Replace(path, @"\[\d*\]", "[]");

    private static bool Wanted(string path, HashSet<string>? wanted)
    {
        if (wanted is null) return true;

        var plain = Unindexed(path);
        if (wanted.Contains(plain)) return true;

        // A value beneath a wanted path: "assignees" wants "assignees[].login".
        for (var i = 0; i < plain.Length; i++)
        {
            if ((plain[i] == '.' || plain[i] == '[') && wanted.Contains(plain[..i])) return true;
        }

        return false;
    }

    private static void Flatten(
        JsonElement element, string prefix, int depth, Dictionary<string, string> row, HashSet<string>? wanted = null)
    {
        if (row.Count >= MaxPathsPerItem) return;

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (depth >= MaxDepth) return;
                foreach (var property in element.EnumerateObject())
                {
                    Flatten(property.Value, Join(prefix, property.Name), depth + 1, row, wanted);
                }
                return;

            case JsonValueKind.Array:
                if (depth >= MaxDepth) return;
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    Flatten(item, $"{prefix}[{index}]", depth + 1, row, wanted);
                    index++;
                    if (row.Count >= MaxPathsPerItem) return;
                }
                return;

            case JsonValueKind.Null or JsonValueKind.Undefined:
                return;

            case JsonValueKind.String:
                if (prefix.Length > 0 && Wanted(prefix, wanted)) row[prefix] = element.GetString() ?? string.Empty;
                return;

            default:
                // Numbers and booleans by their raw text, so a large integer or a decimal keeps the
                // digits the provider sent rather than passing through a double.
                if (prefix.Length > 0 && Wanted(prefix, wanted)) row[prefix] = element.GetRawText();
                return;
        }
    }

    private static string Join(string prefix, string name) =>
        prefix.Length == 0 ? name : $"{prefix}.{name}";

    /// <summary>The names an RSS or Atom entry is read into, whichever of the two it came from.</summary>
    /// <remarks>
    /// One vocabulary for both, so a mapping written against an RSS feed keeps working if the
    /// publisher moves to Atom. The alternative is a mapping that has to know which dialect the
    /// publisher happens to serve this month.
    /// </remarks>
    public static class FeedFields
    {
        public const string Id = "id";
        public const string Title = "title";
        public const string Link = "link";
        public const string Summary = "summary";
        public const string Content = "content";
        public const string Published = "published";
        public const string Author = "author";
        public const string Category = "category";

        public static readonly string[] All =
            [Id, Title, Link, Summary, Content, Published, Author, Category];
    }

    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace ContentModule = "http://purl.org/rss/1.0/modules/content/";
    private static readonly XNamespace DublinCore = "http://purl.org/dc/elements/1.1/";

    /// <summary>Reads the entries out of an RSS 2.0 or Atom feed.</summary>
    public static SyncPayload ReadFeed(string body, int maxItems)
    {
        XDocument document;
        try
        {
            // DTDs off and no resolver. A feed is a document from a third party, and a DOCTYPE in one
            // is either an entity expansion aimed at this process or an external reference that would
            // make the parser fetch a URL of the publisher's choosing from inside the sweep.
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersFromEntities = 0,
            };

            using var reader = XmlReader.Create(new StringReader(body), settings);
            document = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            return SyncPayload.Failed($"The response is not a readable XML feed: {ex.Message}");
        }

        var root = document.Root;
        if (root is null)
        {
            return SyncPayload.Failed("The feed is empty.");
        }

        var items = root.Name == Atom + "feed"
            ? root.Elements(Atom + "entry").Select(ReadAtomEntry)
            : root.Descendants("item").Select(ReadRssItem);

        var read = items.Take(maxItems + 1).ToList();
        var rows = read.Take(maxItems).ToList();

        return rows.Count == 0
            ? SyncPayload.Failed("The feed holds no items. It is neither RSS with <item> nor Atom with <entry>.")
            : new SyncPayload(rows, null) { Truncated = read.Count > maxItems };
    }

    private static IReadOnlyDictionary<string, string> ReadRssItem(XElement item)
    {
        var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var link = Text(item.Element("link"));

        Set(row, FeedFields.Id, Text(item.Element("guid")) is { Length: > 0 } guid ? guid : link);
        Set(row, FeedFields.Title, Text(item.Element("title")));
        Set(row, FeedFields.Link, link);
        Set(row, FeedFields.Summary, Text(item.Element("description")));
        Set(row, FeedFields.Content,
            Text(item.Element(ContentModule + "encoded")) is { Length: > 0 } encoded
                ? encoded
                : Text(item.Element("description")));
        Set(row, FeedFields.Published, Timestamp(Text(item.Element("pubDate"))));
        Set(row, FeedFields.Author,
            Text(item.Element(DublinCore + "creator")) is { Length: > 0 } creator
                ? creator
                : Text(item.Element("author")));
        Set(row, FeedFields.Category, Text(item.Element("category")));

        return row;
    }

    private static IReadOnlyDictionary<string, string> ReadAtomEntry(XElement entry)
    {
        var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // The alternate link, which is the entry's page. A feed carries several, and taking the
        // first would as happily hand back the "replies" or "edit" link.
        var link = entry.Elements(Atom + "link")
            .FirstOrDefault(l => (string?)l.Attribute("rel") is null or "alternate")
            ?? entry.Element(Atom + "link");

        Set(row, FeedFields.Id, Text(entry.Element(Atom + "id")));
        Set(row, FeedFields.Title, Text(entry.Element(Atom + "title")));
        Set(row, FeedFields.Link, (string?)link?.Attribute("href") ?? string.Empty);
        Set(row, FeedFields.Summary, Text(entry.Element(Atom + "summary")));
        Set(row, FeedFields.Content, Text(entry.Element(Atom + "content")));
        Set(row, FeedFields.Published,
            Timestamp(Text(entry.Element(Atom + "published")) is { Length: > 0 } published
                ? published
                : Text(entry.Element(Atom + "updated"))));
        Set(row, FeedFields.Author, Text(entry.Element(Atom + "author")?.Element(Atom + "name")));
        Set(row, FeedFields.Category, (string?)entry.Element(Atom + "category")?.Attribute("term") ?? string.Empty);

        return row;
    }

    private static void Set(Dictionary<string, string> row, string key, string value)
    {
        if (value.Length > 0) row[key] = value;
    }

    private static string Text(XElement? element) => element?.Value.Trim() ?? string.Empty;

    /// <summary>
    /// A feed date as UTC ISO 8601, or the original text when it does not parse as a date.
    /// </summary>
    /// <remarks>
    /// RSS spells a date RFC 822 and Atom spells it RFC 3339, so a mapping into a <c>datetime</c>
    /// field would otherwise depend on which dialect the publisher serves. Unparseable text is
    /// passed through rather than dropped: the operator may be mapping it into a string field, and
    /// deciding it is not a date here would silently empty it.
    /// </remarks>
    private static string Timestamp(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)
            : value;
}
