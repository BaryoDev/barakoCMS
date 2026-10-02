using System.Net;
using System.Net.Http.Json;
using barakoCMS.Models;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests.Features.Collections;

/// <summary>
/// A collection sync writes entries without the entry validator, so it applies the money rule
/// itself: an item whose amount has more decimal places than the field's currency takes is skipped,
/// the way an item that does not convert to its field's type is.
/// </summary>
[Collection("Sequential")]
public class CollectionSyncMoneyTests
{
    private const string Priced = """
    {
      "data": [
        { "id": "Fits",    "price": 12.50 },
        { "id": "TooFine", "price": 9.999 }
      ]
    }
    """;

    private readonly CollectionSyncTests _syncs;

    public CollectionSyncMoneyTests(IntegrationTestFixture factory) => _syncs = new CollectionSyncTests(factory);

    private async Task<CollectionSyncTests.Setup> ArrangeAsync(string? currency)
    {
        var setup = await _syncs.ArrangeAsync(
            () => (HttpStatusCode.OK, Priced),
            save: false,
            fields:
            [
                new FieldDefinition { Name = "packageId", Type = "string" },
                new FieldDefinition { Name = "price", Type = "money", Currency = currency },
            ]);

        var body = CollectionSyncTests.SyncBody(setup);
        body["fieldMap"] = new Dictionary<string, string> { ["packageId"] = "id", ["price"] = "price" };

        var saved = await (await _syncs.AdminAsync()).PostAsJsonAsync(
            "/api/collection-syncs", body, TestContext.Current.CancellationToken);
        saved.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", saved.StatusCode,
            await saved.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        return setup;
    }

    [Fact]
    public async Task An_item_whose_amount_does_not_fit_the_fields_currency_is_skipped_and_the_rest_are_written()
    {
        var setup = await ArrangeAsync("USD");

        var outcome = await _syncs.RunAsync(setup);

        outcome.GetProperty("created").GetInt32().Should().Be(1);
        outcome.GetProperty("skipped").GetInt32().Should().Be(1);

        var entries = await _syncs.EntriesAsync(setup.Type);
        entries.Should().HaveCount(1);
        entries[0].Data["packageId"].ToString().Should().Be("Fits");
        entries[0].Data["price"].Should().Be(12.50m);
    }

    [Fact]
    public async Task A_money_field_with_no_currency_takes_both_items_as_it_did()
    {
        var setup = await ArrangeAsync(null);

        var outcome = await _syncs.RunAsync(setup);

        outcome.GetProperty("created").GetInt32().Should().Be(2);
        outcome.GetProperty("skipped").GetInt32().Should().Be(0);
        (await _syncs.EntriesAsync(setup.Type)).Should().HaveCount(2);
    }
}
