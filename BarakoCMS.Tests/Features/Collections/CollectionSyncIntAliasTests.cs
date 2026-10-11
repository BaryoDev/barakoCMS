using System.Net;
using System.Net.Http.Json;
using barakoCMS.Models;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests.Features.Collections;

/// <summary>
/// A sync keeps the cell text for a field declared <c>number</c> or <c>integer</c>, as it always
/// has, while an <c>int</c> field takes a whole number past Int32 (#706).
/// </summary>
/// <remarks>
/// Converting the aliases would skip an item whose cell holds 3.5, which syncs today, and turn
/// stored text into numbers on the next run.
/// </remarks>
[Collection("Sequential")]
public class CollectionSyncIntAliasTests
{
    private const string Rated = """
    {
      "data": [
        { "id": "Pkg", "rating": 3.5, "label": "n/a", "count": 3000000000 }
      ]
    }
    """;

    private readonly CollectionSyncTests _syncs;

    public CollectionSyncIntAliasTests(IntegrationTestFixture factory) => _syncs = new CollectionSyncTests(factory);

    [Fact]
    public async Task A_number_or_integer_field_stores_the_cell_text_and_an_int_field_takes_a_value_past_Int32()
    {
        var setup = await _syncs.ArrangeAsync(
            () => (HttpStatusCode.OK, Rated),
            save: false,
            fields:
            [
                new FieldDefinition { Name = "packageId", Type = "string" },
                new FieldDefinition { Name = "rating", Type = "number" },
                new FieldDefinition { Name = "label", Type = "integer" },
                new FieldDefinition { Name = "count", Type = "int" },
            ]);

        var body = CollectionSyncTests.SyncBody(setup);
        body["fieldMap"] = new Dictionary<string, string>
        {
            ["packageId"] = "id", ["rating"] = "rating", ["label"] = "label", ["count"] = "count",
        };

        var saved = await (await _syncs.AdminAsync()).PostAsJsonAsync(
            "/api/collection-syncs", body, TestContext.Current.CancellationToken);
        saved.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", saved.StatusCode,
            await saved.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var outcome = await _syncs.RunAsync(setup);

        outcome.GetProperty("created").GetInt32().Should().Be(1);
        outcome.GetProperty("skipped").GetInt32().Should().Be(0);

        var entries = await _syncs.EntriesAsync(setup.Type);
        entries.Should().HaveCount(1);
        entries[0].Data["rating"].Should().BeOfType<string>().Which.Should().Be("3.5");
        entries[0].Data["label"].Should().BeOfType<string>().Which.Should().Be("n/a");
        entries[0].Data["count"].Should().Be(3_000_000_000L);
    }
}
