using System.Net;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using BarakoCMS.Tests.Builders;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The RSS feed description used to wrap the raw field value in a CDATA block, escaping only
/// "]]&gt;". Many readers render a feed description as HTML, so a Body of
/// &lt;img src=x onerror=...&gt; in authored content became stored XSS in every subscriber's reader.
/// The description must be entity-encoded like the title, so authored markup shows as text and never
/// executes.
/// </summary>
[Collection("Sequential")]
public class FeedEscapingTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;

    public FeedEscapingTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Feed_description_is_entity_encoded_not_raw_html()
    {
        var type = $"feed_xss_{Guid.NewGuid():N}";
        var payload = "<img src=x onerror=alert(1)><script>alert(2)</script>";

        using (var scope = _factory.Services.CreateScope())
        {
            var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            s.Store(new ContentTypeBuilder()
                .Named(type)
                .PubliclyDeliverable()
                .WithTitleAndSlug()
                .WithField("Excerpt")
                .Build());
            s.Store(new ContentBuilder()
                .OfType(type)
                .WithTitleAndSlug("A Post", "a-post")
                .With("Excerpt", payload)
                .Published()
                .Build());
            await s.SaveChangesAsync();
        }

        var res = await _client.GetAsync($"/api/public/{type}/feed.xml");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var xml = await res.Content.ReadAsStringAsync();

        // the raw, executable markup must not appear
        xml.Should().NotContain("<img src=x onerror", "the payload must not reach a reader as live HTML");
        xml.Should().NotContain("<script>alert(2)", "no raw script tag in the feed");
        xml.Should().NotContain("<![CDATA[", "the description no longer passes content through verbatim");
        // the content is still present, encoded
        xml.Should().Contain("&lt;img src=x onerror", "the value is shown as text, entity-encoded");
    }
}
