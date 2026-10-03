using System.Net;
using System.Net.Http.Json;
using barakoCMS.Models;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests.Features.Collections;

/// <summary>
/// A collection sync writes entries without the entry validator and maps text, so a mapping onto an
/// inline image field is refused when the sync is saved.
/// </summary>
[Collection("Sequential")]
public class CollectionSyncInlineImageTests
{
    private readonly CollectionSyncTests _syncs;

    public CollectionSyncInlineImageTests(IntegrationTestFixture factory) => _syncs = new CollectionSyncTests(factory);

    [Fact]
    public async Task A_sync_mapping_a_value_onto_an_inline_image_field_is_refused_and_the_same_sync_without_it_is_saved()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await _syncs.ArrangeAsync(
            () => (HttpStatusCode.OK, """{ "data": [] }"""),
            save: false,
            fields:
            [
                new FieldDefinition { Name = "packageId", Type = "string" },
                new FieldDefinition { Name = "logo", Type = "inlineimage" },
            ]);
        var client = await _syncs.AdminAsync();

        var body = CollectionSyncTests.SyncBody(setup);
        body["fieldMap"] = new Dictionary<string, string> { ["packageId"] = "id", ["logo"] = "icon" };

        var refused = await client.PostAsJsonAsync("/api/collection-syncs", body, ct);

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await refused.Content.ReadAsStringAsync(ct)).Should().Contain("is an inline image");

        body["fieldMap"] = new Dictionary<string, string> { ["packageId"] = "id" };

        var saved = await client.PostAsJsonAsync("/api/collection-syncs", body, ct);
        saved.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", saved.StatusCode,
            await saved.Content.ReadAsStringAsync(ct));
    }
}
