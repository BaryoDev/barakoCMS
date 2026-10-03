using System.Net;
using System.Net.Http.Json;
using barakoCMS.Models;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests.Features.Collections;

/// <summary>
/// A collection sync writes entries without the entry validator and for no signed-in user, so it
/// may not fill a <c>file</c> field: there is nobody to ask the file store as.
/// </summary>
[Collection("Sequential")]
public class CollectionSyncFileTests
{
    private const string Items = """
    {
      "data": [
        { "id": "One", "cover": "6f9619ff-8b86-d011-b42d-00cf4fc964ff" }
      ]
    }
    """;

    private readonly CollectionSyncTests _syncs;

    public CollectionSyncFileTests(IntegrationTestFixture factory) => _syncs = new CollectionSyncTests(factory);

    [Fact]
    public async Task A_sync_mapping_a_source_value_onto_a_file_field_is_refused_and_one_leaving_it_alone_is_saved()
    {
        var setup = await _syncs.ArrangeAsync(
            () => (HttpStatusCode.OK, Items),
            save: false,
            fields:
            [
                new FieldDefinition { Name = "packageId", Type = "string" },
                new FieldDefinition { Name = "cover", Type = "file" },
            ]);

        var admin = await _syncs.AdminAsync();

        var mapped = CollectionSyncTests.SyncBody(setup);
        mapped["fieldMap"] = new Dictionary<string, string> { ["packageId"] = "id", ["cover"] = "cover" };

        var refused = await admin.PostAsJsonAsync("/api/collection-syncs", mapped, TestContext.Current.CancellationToken);
        var body = await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("cover").And.Contain("file field");

        // The control: the same sync without the file field is saved, and runs.
        var unmapped = CollectionSyncTests.SyncBody(setup);
        unmapped["fieldMap"] = new Dictionary<string, string> { ["packageId"] = "id" };

        var saved = await admin.PostAsJsonAsync("/api/collection-syncs", unmapped, TestContext.Current.CancellationToken);
        saved.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", saved.StatusCode,
            await saved.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var outcome = await _syncs.RunAsync(setup);
        outcome.GetProperty("created").GetInt32().Should().Be(1);

        var entries = await _syncs.EntriesAsync(setup.Type);
        entries.Should().ContainSingle();
        entries[0].Data.Should().NotContainKey("cover");
    }
}
