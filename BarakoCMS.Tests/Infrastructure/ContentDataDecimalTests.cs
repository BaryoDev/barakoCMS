using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Infrastructure;

/// <summary>
/// Money in a content type's <c>Data</c> bag must survive a real Marten round-trip as
/// <see cref="decimal"/>. <see cref="ObjectJsonConverterTests"/> pins the converter's rules without a
/// database; these prove the converter is actually wired into the store — the gap that let the
/// original <c>double</c> behaviour ship unnoticed.
/// </summary>
[Collection("Sequential")]
public class ContentDataDecimalTests
{
    private readonly IntegrationTestFixture _factory;
    public ContentDataDecimalTests(IntegrationTestFixture factory) => _factory = factory;

    private async Task<Dictionary<string, object>> RoundTripAsync(Dictionary<string, object> data)
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        var id = Guid.NewGuid();

        using (var session = store.LightweightSession())
        {
            session.Store(new Content
            {
                Id = id,
                ContentType = $"probe_{Guid.NewGuid():N}",
                Data = data,
            });
            await session.SaveChangesAsync();
        }

        using var read = store.QuerySession();
        return (await read.LoadAsync<Content>(id))!.Data;
    }

    [Fact]
    public async Task Money_round_trips_as_decimal_not_double()
    {
        var data = await RoundTripAsync(new Dictionary<string, object> { ["Amount"] = 1234.56m });

        data["Amount"].Should().BeOfType<decimal>(
            "a ledger amount stored as double accumulates drift when summed across many lines");
        data["Amount"].Should().Be(1234.56m);
    }

    [Fact]
    public async Task Whole_numbers_still_come_back_integral()
    {
        var data = await RoundTripAsync(new Dictionary<string, object> { ["Count"] = 42 });

        data["Count"].Should().Be(42L, "ids and counts must not become 42.0m");
    }

    [Fact]
    public async Task Nested_values_are_plain_clr_types_not_json_elements()
    {
        var data = await RoundTripAsync(new Dictionary<string, object>
        {
            ["Nested"] = new Dictionary<string, object> { ["Inner"] = 9.99m },
            ["Lines"] = new List<object> { new Dictionary<string, object> { ["Debit"] = 10.05m } },
        });

        var nested = data["Nested"].Should().BeOfType<Dictionary<string, object>>().Subject;
        nested["Inner"].Should().BeOfType<decimal>().And.Be(9.99m);

        var lines = data["Lines"].Should().BeOfType<List<object>>().Subject;
        lines[0].Should().BeOfType<Dictionary<string, object>>()
            .Which["Debit"].Should().BeOfType<decimal>().And.Be(10.05m);
    }

    [Fact]
    public async Task Summing_many_round_tripped_amounts_stays_exact()
    {
        // The failure this guards: 0.1 has no exact binary form, so a double-backed ledger drifts.
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        var contentType = $"ledger_{Guid.NewGuid():N}";
        var ids = new List<Guid>();

        using (var session = store.LightweightSession())
        {
            for (var i = 0; i < 10; i++)
            {
                var id = Guid.NewGuid();
                ids.Add(id);
                session.Store(new Content
                {
                    Id = id,
                    ContentType = contentType,
                    Data = new Dictionary<string, object> { ["Amount"] = 0.1m },
                });
            }
            await session.SaveChangesAsync();
        }

        using var read = store.QuerySession();
        decimal sum = 0;
        foreach (var id in ids)
        {
            var amount = (await read.LoadAsync<Content>(id))!.Data["Amount"];
            amount.Should().BeOfType<decimal>();
            sum += (decimal)amount;
        }

        sum.Should().Be(1.0m, "ten 0.1 postings must total exactly 1.00, not 0.9999999999999999");
    }
}
