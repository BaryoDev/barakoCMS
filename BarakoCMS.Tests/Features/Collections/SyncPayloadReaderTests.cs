using barakoCMS.Infrastructure.Sync;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests.Features.Collections;

/// <summary>
/// Reading a provider's answer into rows a field mapping can address (#794).
/// </summary>
/// <remarks>
/// Every one of these is a real shape: the NuGet search envelope, an RSS item, an Atom entry, and a
/// feed with a DOCTYPE in it. None of them needs a network or a database to reproduce, which is the
/// reason the reader is a pure static in the first place.
/// </remarks>
public class SyncPayloadReaderTests
{
    private const string NuGetSearch = """
    {
      "totalHits": 2,
      "data": [
        { "id": "BarakoCMS", "totalDownloads": 1200, "description": "Headless CMS",
          "authors": ["BaryoDev"], "versions": [ { "version": "4.2.1", "downloads": 40 } ] },
        { "id": "BarakoCMS.Files", "totalDownloads": 310, "description": "File storage",
          "authors": ["BaryoDev"] }
      ]
    }
    """;

    [Fact]
    public void A_nuget_search_response_yields_one_row_per_package()
    {
        var payload = SyncPayloadReader.ReadJson(NuGetSearch, "data", maxItems: 100);

        payload.Error.Should().BeNull();
        payload.Rows.Should().HaveCount(2, "the reader has to find something for the rest of this to mean anything");

        payload.Rows[0]["id"].Should().Be("BarakoCMS");
        payload.Rows[0]["totalDownloads"].Should().Be("1200");
        payload.Rows[0]["authors[0]"].Should().Be("BaryoDev");
        payload.Rows[0]["versions[0].version"].Should().Be("4.2.1", "a nested path is addressable");
        payload.Rows[1]["id"].Should().Be("BarakoCMS.Files");
    }

    /// <summary>
    /// An items path that is not in the response is reported, not read as an empty list.
    /// </summary>
    /// <remarks>
    /// This is the difference between a sync that says it is broken and one that reports a
    /// successful run of zero entries every hour while the page it fills stays empty.
    /// </remarks>
    [Fact]
    public void An_items_path_that_is_not_there_is_an_error_rather_than_no_rows()
    {
        var payload = SyncPayloadReader.ReadJson(NuGetSearch, "results", maxItems: 100);

        payload.Ok.Should().BeFalse();
        payload.Error.Should().Contain("results");
        payload.Rows.Should().BeEmpty();
    }

    [Fact]
    public void A_response_that_is_itself_an_array_needs_no_items_path()
    {
        var payload = SyncPayloadReader.ReadJson("""[{"name":"one"},{"name":"two"}]""", "", maxItems: 100);

        payload.Rows.Should().HaveCount(2);
        payload.Rows[1]["name"].Should().Be("two");
    }

    [Fact]
    public void More_items_than_the_cap_are_ignored()
    {
        var many = "[" + string.Join(",", Enumerable.Range(0, 50).Select(i => $$"""{"n":{{i}}}""")) + "]";

        var payload = SyncPayloadReader.ReadJson(many, "", maxItems: 5);

        payload.Rows.Should().HaveCount(5, "a source that answers fifty must not become fifty writes");
        payload.Rows[4]["n"].Should().Be("4");
    }

    [Fact]
    public void A_body_that_is_not_json_is_refused_without_quoting_it()
    {
        var payload = SyncPayloadReader.ReadJson("<html>signed out</html>", "data", maxItems: 100);

        payload.Ok.Should().BeFalse();
        payload.Error.Should().Contain("not valid JSON");
        payload.Error.Should().NotContain("signed out", "a stored error must not carry the provider's body");
    }

    private const string Rss = """
    <?xml version="1.0"?>
    <rss version="2.0" xmlns:dc="http://purl.org/dc/elements/1.1/">
      <channel>
        <title>Rotary International</title>
        <item>
          <title>Rotary at work</title>
          <link>https://rotary.example/news/1</link>
          <guid>rotary-1</guid>
          <description>What happened this month</description>
          <pubDate>Tue, 02 Sep 2025 08:30:00 GMT</pubDate>
          <dc:creator>Rotary</dc:creator>
          <category>News</category>
        </item>
      </channel>
    </rss>
    """;

    private const string Atom = """
    <?xml version="1.0"?>
    <feed xmlns="http://www.w3.org/2005/Atom">
      <title>Medium</title>
      <entry>
        <id>rotary-1</id>
        <title>Rotary at work</title>
        <link rel="edit" href="https://medium.example/edit/1"/>
        <link rel="alternate" href="https://rotary.example/news/1"/>
        <summary>What happened this month</summary>
        <published>2025-09-02T08:30:00Z</published>
        <author><name>Rotary</name></author>
        <category term="News"/>
      </entry>
    </feed>
    """;

    /// <summary>
    /// RSS and Atom read into the same names, so a mapping survives a publisher changing dialect.
    /// </summary>
    [Fact]
    public void An_rss_item_and_an_atom_entry_read_into_the_same_field_names()
    {
        var rss = SyncPayloadReader.ReadFeed(Rss, maxItems: 100);
        var atom = SyncPayloadReader.ReadFeed(Atom, maxItems: 100);

        rss.Rows.Should().HaveCount(1);
        atom.Rows.Should().HaveCount(1);

        foreach (var field in new[] { "id", "title", "link", "summary", "published", "author", "category" })
        {
            atom.Rows[0][field].Should().Be(rss.Rows[0][field], "'{0}' has to read the same either way", field);
        }

        rss.Rows[0]["title"].Should().Be("Rotary at work");
        rss.Rows[0]["link"].Should().Be("https://rotary.example/news/1");
        rss.Rows[0]["published"].Should().StartWith("2025-09-02T08:30:00",
            "RFC 822 and RFC 3339 both have to land as UTC ISO 8601 for a datetime field to take them");
    }

    /// <summary>
    /// An Atom entry's alternate link is the entry's page, not whichever link came first.
    /// </summary>
    /// <remarks>
    /// Taking the first would have taken the edit link above, and a collection of links that point
    /// at a publisher's editor rather than at the article is wrong in a way nobody notices until a
    /// reader clicks one.
    /// </remarks>
    [Fact]
    public void An_atom_entrys_link_is_the_alternate_one()
    {
        var atom = SyncPayloadReader.ReadFeed(Atom, maxItems: 100);

        atom.Rows.Should().HaveCount(1);
        atom.Rows[0]["link"].Should().Be("https://rotary.example/news/1");
        atom.Rows[0]["link"].Should().NotContain("edit");
    }

    /// <summary>
    /// A feed carrying a DOCTYPE is refused rather than parsed.
    /// </summary>
    /// <remarks>
    /// This is the whole reason the reader configures its own <c>XmlReaderSettings</c>. A feed comes
    /// from a third party, and a DOCTYPE in one is either an entity expansion aimed at this process
    /// or an external reference that would have the sweep fetch a URL of the publisher's choosing.
    /// </remarks>
    [Fact]
    public void A_feed_carrying_a_doctype_is_refused()
    {
        var hostile = """
        <?xml version="1.0"?>
        <!DOCTYPE rss [ <!ENTITY xxe SYSTEM "file:///etc/passwd"> ]>
        <rss version="2.0"><channel><item><title>&xxe;</title></item></channel></rss>
        """;

        var payload = SyncPayloadReader.ReadFeed(hostile, maxItems: 100);

        payload.Ok.Should().BeFalse();
        payload.Rows.Should().BeEmpty();
    }

    [Fact]
    public void A_document_that_is_neither_rss_nor_atom_is_reported()
    {
        var payload = SyncPayloadReader.ReadFeed("<html><body>nope</body></html>", maxItems: 100);

        payload.Ok.Should().BeFalse();
        payload.Error.Should().Contain("no items");
    }
}
