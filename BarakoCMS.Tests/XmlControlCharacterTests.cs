using System.Net;
using System.Xml.Linq;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using BarakoCMS.Tests.Builders;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// A character outside the XML 1.0 range makes the whole document not well formed, so a reader
/// refuses the feed. The title is stored directly, which is how a value the validator never saw
/// reaches the feed.
/// </summary>
[Collection("Integration1")]
public class XmlControlCharacterTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;

    public XmlControlCharacterTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task A_control_character_in_a_title_leaves_the_feed_well_formed()
    {
        var ct = TestContext.Current.CancellationToken;
        var type = "xmlctl" + Guid.NewGuid().ToString("n")[..10];

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new ContentTypeBuilder().Named(type).PubliclyDeliverable().WithTitleAndSlug().Build());
            session.Store(new ContentBuilder()
                .OfType(type)
                .WithTitleAndSlug("Bad\u0008 title", "bad-title")
                .Published()
                .Build());
            await session.SaveChangesAsync(ct);
        }

        var res = await _client.GetAsync($"/api/public/{type}/feed.xml", ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var xml = await res.Content.ReadAsStringAsync(ct);

        var doc = XDocument.Parse(xml);
        var items = doc.Descendants("item").ToList();
        items.Should().HaveCount(1);
        items[0].Element("title")!.Value.Should().Contain("title");
    }
}
