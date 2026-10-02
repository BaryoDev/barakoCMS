using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Models;
using Xunit;

namespace BarakoCMS.Tests.Features.ContentTypes;

/// <summary>
/// A money field that declares a currency, over HTTP: declared when the field is made, or later on
/// a field that already has entries.
/// </summary>
/// <remarks>
/// The amount stays a plain number in the entry, so the tests that store entries first and declare
/// the currency afterwards are the ones about existing data: nothing is rewritten, and what does
/// not fit is counted and reported before the change lands.
/// </remarks>
[Collection("Sequential")]
public class MoneyFieldTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;
    private readonly HttpClient _client;

    public MoneyFieldTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.CreateClient();
    }

    public async ValueTask InitializeAsync() =>
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", await _fixture.StoredUserTokenAsync("Admin", "SuperAdmin"));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static string NewName() => "money-" + Guid.NewGuid().ToString("n")[..12];

    private static FieldDefinition Price(string? currency = null, int? scale = null) => new()
    {
        Name = "Price", DisplayName = "Price", Type = "money", Currency = currency, Scale = scale,
    };

    private async Task<string> StoreTypeAsync(bool deliverable, params FieldDefinition[] fields)
    {
        var name = NewName();
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(), Name = name, DisplayName = name, IsPubliclyDeliverable = deliverable,
            Fields = [new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" }, .. fields],
        });
        await session.SaveChangesAsync();
        return name;
    }

    private async Task StoreEntryAsync(string type, string title, object? price)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var data = new Dictionary<string, object> { ["Title"] = title };
        if (price is not null) data["Price"] = price;
        session.Store(new Content
        {
            Id = Guid.NewGuid(), ContentType = type, Status = ContentStatus.Published,
            Sensitivity = SensitivityLevel.Public, Data = data,
        });
        await session.SaveChangesAsync();
    }

    private Task<HttpResponseMessage> CreateEntryAsync(string type, string title, object price) =>
        _client.PostAsJsonAsync("/api/contents", new
        {
            contentType = type,
            data = new Dictionary<string, object> { ["Title"] = title, ["Price"] = price },
        });

    private Task<HttpResponseMessage> PutCurrencyAsync(string type, string field, object body) =>
        _client.PutAsJsonAsync($"/api/content-types/{type}/fields/{field}/currency", body);

    private async Task<FieldDefinition> ReadFieldAsync(string type, string field)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var def = await session.Query<ContentTypeDefinition>().SingleAsync(d => d.Name == type);
        return def.Fields.Single(f => f.Name == field);
    }

    // ---- declared with the field -----------------------------------------------------------

    [Fact]
    public async Task Adding_a_money_field_carries_its_currency_and_entries_are_held_to_it()
    {
        var type = await StoreTypeAsync(false);

        var added = await _client.PostAsJsonAsync($"/api/content-types/{type}/fields", new
        {
            fieldName = "Price", type = "money", currency = "USD",
        });
        added.StatusCode.Should().Be(HttpStatusCode.OK, await added.Content.ReadAsStringAsync());
        (await ReadFieldAsync(type, "Price")).Currency.Should().Be("USD");

        var tooFine = await CreateEntryAsync(type, "a", 9.999);
        tooFine.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await tooFine.Content.ReadAsStringAsync()).Should().Contain("Price").And.Contain("USD").And.Contain("2 decimal places");

        var fits = await CreateEntryAsync(type, "b", 9.99);
        fits.IsSuccessStatusCode.Should().BeTrue(await fits.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Creating_a_type_stores_the_currency_and_scale_its_field_declares_and_the_schema_returns_them()
    {
        var name = NewName();

        var created = await _client.PostAsJsonAsync("/api/content-types", new
        {
            name, displayName = "Priced",
            fields = new object[]
            {
                new { name = "Price", displayName = "Price", type = "money", currency = "USD", scale = 4 },
                new { name = "Cost", displayName = "Cost", type = "money" },
            },
        });
        created.IsSuccessStatusCode.Should().BeTrue(await created.Content.ReadAsStringAsync());

        var stored = await ReadFieldAsync(name, "Price");
        stored.Currency.Should().Be("USD");
        stored.Scale.Should().Be(4);

        var res = await _client.GetAsync("/api/content-types");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        var list = root.ValueKind == JsonValueKind.Array ? root : root.GetProperty("items");
        var fields = list.EnumerateArray().Single(t => t.GetProperty("name").GetString() == name)
            .GetProperty("fields").EnumerateArray()
            .ToDictionary(f => f.GetProperty("name").GetString()!, f => f);
        fields.Should().HaveCount(2);

        fields["Price"].GetProperty("currency").GetString().Should().Be("USD");
        fields["Price"].GetProperty("scale").GetInt32().Should().Be(4);
        fields["Cost"].GetProperty("currency").ValueKind.Should().Be(JsonValueKind.Null);
        fields["Cost"].GetProperty("scale").ValueKind.Should().Be(JsonValueKind.Null);

        (await CreateEntryAsync(name, "a", 1.2345)).IsSuccessStatusCode.Should().BeTrue();
        (await CreateEntryAsync(name, "b", 1.23456)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_type_is_refused_when_a_field_declares_a_currency_no_scale_is_known_for()
    {
        var unknown = await _client.PostAsJsonAsync("/api/content-types", new
        {
            name = NewName(), displayName = "Unknown code",
            fields = new[] { new { name = "Price", type = "money", currency = "QQQ" } },
        });
        unknown.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await unknown.Content.ReadAsStringAsync()).Should().Contain("Declare a scale");

        var wrongType = await _client.PostAsJsonAsync("/api/content-types", new
        {
            name = NewName(), displayName = "Wrong type",
            fields = new[] { new { name = "Price", type = "decimal", currency = "USD" } },
        });
        wrongType.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await wrongType.Content.ReadAsStringAsync()).Should().Contain("not money");
    }

    [Fact]
    public async Task A_money_field_with_no_currency_still_takes_any_number_of_decimal_places()
    {
        var type = await StoreTypeAsync(false, Price());

        var res = await CreateEntryAsync(type, "a", 9.999);

        res.IsSuccessStatusCode.Should().BeTrue(await res.Content.ReadAsStringAsync());
    }

    // ---- declared on a field that already has entries --------------------------------------

    [Fact]
    public async Task Declaring_a_currency_is_refused_while_entries_hold_amounts_that_do_not_fit_unless_forced()
    {
        var type = await StoreTypeAsync(false, Price());
        await StoreEntryAsync(type, "fits", 10m);
        await StoreEntryAsync(type, "also fits", 10.50m);
        await StoreEntryAsync(type, "too fine", 10.005m);
        await StoreEntryAsync(type, "none", null);

        var refused = await PutCurrencyAsync(type, "Price", new { currency = "USD" });
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict, await refused.Content.ReadAsStringAsync());
        (await refused.Content.ReadAsStringAsync()).Should().Contain("1 entry holds").And.Contain("force");
        (await ReadFieldAsync(type, "Price")).Currency.Should().BeNull("a refused change must not land");

        var forced = await PutCurrencyAsync(type, "Price", new { currency = "USD", force = true });
        forced.StatusCode.Should().Be(HttpStatusCode.OK, await forced.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await forced.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("entriesNotFitting").GetInt32().Should().Be(1);
        body.RootElement.GetProperty("currency").GetString().Should().Be("USD");
        body.RootElement.GetProperty("scale").GetInt32().Should().Be(2);
        (await ReadFieldAsync(type, "Price")).Currency.Should().Be("USD");
    }

    [Fact]
    public async Task Declaring_a_currency_every_entry_fits_needs_no_force_and_then_holds_new_entries_to_it()
    {
        var type = await StoreTypeAsync(false, Price());
        await StoreEntryAsync(type, "one", 10m);
        await StoreEntryAsync(type, "two", 10.5m);
        await StoreEntryAsync(type, "none", null);

        var declared = await PutCurrencyAsync(type, "Price", new { currency = "USD" });
        declared.StatusCode.Should().Be(HttpStatusCode.OK, await declared.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await declared.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("entriesNotFitting").GetInt32().Should().Be(0);

        (await CreateEntryAsync(type, "too fine", 1.005)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var fits = await CreateEntryAsync(type, "fits", 1.05);
        fits.IsSuccessStatusCode.Should().BeTrue(await fits.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Entries_stored_before_the_currency_keep_filtering_and_returning_their_plain_number()
    {
        var type = await StoreTypeAsync(true, Price());
        await StoreEntryAsync(type, "cheap", 5m);
        await StoreEntryAsync(type, "odd", 10.005m);
        await StoreEntryAsync(type, "dear", 20m);

        var forced = await PutCurrencyAsync(type, "Price", new { currency = "USD", force = true });
        forced.StatusCode.Should().Be(HttpStatusCode.OK, await forced.Content.ReadAsStringAsync());

        var res = await _fixture.CreateClient().GetAsync($"/api/public/{type}?filter[Price][gte]=10");
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var items = doc.RootElement.GetProperty("items").EnumerateArray().ToList();
        items.Should().HaveCount(2);

        var prices = items
            .Select(i => i.GetProperty("data").GetProperty("Price"))
            .ToList();
        prices.Should().OnlyContain(p => p.ValueKind == JsonValueKind.Number);
        prices.Select(p => p.GetDecimal()).Should().BeEquivalentTo(new[] { 10.005m, 20m });
    }

    [Fact]
    public async Task Changing_from_one_code_to_another_is_refused_while_entries_hold_amounts_unless_forced()
    {
        var type = await StoreTypeAsync(false, Price("USD"));
        await StoreEntryAsync(type, "one", 10m);
        await StoreEntryAsync(type, "none", null);

        var refused = await PutCurrencyAsync(type, "Price", new { currency = "EUR" });
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict, await refused.Content.ReadAsStringAsync());
        (await refused.Content.ReadAsStringAsync()).Should().Contain("1 entry holds").And.Contain("read as EUR");
        (await ReadFieldAsync(type, "Price")).Currency.Should().Be("USD");

        var forced = await PutCurrencyAsync(type, "Price", new { currency = "EUR", force = true });
        forced.StatusCode.Should().Be(HttpStatusCode.OK, await forced.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await forced.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("entriesRelabelled").GetInt32().Should().Be(1);
        (await ReadFieldAsync(type, "Price")).Currency.Should().Be("EUR");
    }

    [Fact]
    public async Task An_entry_holding_text_in_the_field_counts_as_one_that_does_not_fit()
    {
        var type = await StoreTypeAsync(false, Price());
        await StoreEntryAsync(type, "number", 12.50m);
        await StoreEntryAsync(type, "text", "12.50");

        var refused = await PutCurrencyAsync(type, "Price", new { currency = "USD" });

        refused.StatusCode.Should().Be(HttpStatusCode.Conflict, await refused.Content.ReadAsStringAsync());
        (await refused.Content.ReadAsStringAsync()).Should().Contain("1 entry holds").And.Contain("not a number");
        (await ReadFieldAsync(type, "Price")).Currency.Should().BeNull();
    }

    [Fact]
    public async Task Narrowing_only_the_scale_counts_what_no_longer_fits_and_widening_it_needs_no_force()
    {
        var type = await StoreTypeAsync(false, Price("USD", 4));
        await StoreEntryAsync(type, "fine", 1.2345m);
        await StoreEntryAsync(type, "round", 1.25m);

        var narrowed = await PutCurrencyAsync(type, "Price", new { currency = "USD" });
        narrowed.StatusCode.Should().Be(HttpStatusCode.Conflict, await narrowed.Content.ReadAsStringAsync());
        (await narrowed.Content.ReadAsStringAsync()).Should().Contain("1 entry holds").And.Contain("2 decimal places");
        (await ReadFieldAsync(type, "Price")).Scale.Should().Be(4, "a refused change must not land");

        var widened = await PutCurrencyAsync(type, "Price", new { currency = "USD", scale = 6 });
        widened.StatusCode.Should().Be(HttpStatusCode.OK, await widened.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await widened.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("entriesNotFitting").GetInt32().Should().Be(0);
        body.RootElement.GetProperty("entriesRelabelled").GetInt32().Should().Be(0, "the code did not change");
        (await ReadFieldAsync(type, "Price")).Scale.Should().Be(6);
    }

    [Fact]
    public async Task Clearing_the_currency_returns_the_field_to_a_plain_number()
    {
        var type = await StoreTypeAsync(false, Price("USD"));
        (await CreateEntryAsync(type, "a", 9.999)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var cleared = await PutCurrencyAsync(type, "Price", new { currency = (string?)null });
        cleared.StatusCode.Should().Be(HttpStatusCode.OK, await cleared.Content.ReadAsStringAsync());
        (await ReadFieldAsync(type, "Price")).Currency.Should().BeNull();

        var res = await CreateEntryAsync(type, "b", 9.999);
        res.IsSuccessStatusCode.Should().BeTrue(await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_currency_can_only_be_set_on_a_money_field_that_exists_and_must_be_a_code()
    {
        var type = await StoreTypeAsync(false, Price());

        (await PutCurrencyAsync(type, "Title", new { currency = "USD" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await PutCurrencyAsync(type, "Missing", new { currency = "USD" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await PutCurrencyAsync(type, "Price", new { currency = "usd" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await PutCurrencyAsync(type, "Price", new { currency = "QQQ" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadFieldAsync(type, "Price")).Currency.Should().BeNull();

        var withScale = await PutCurrencyAsync(type, "Price", new { currency = "QQQ", scale = 8 });
        withScale.StatusCode.Should().Be(HttpStatusCode.OK, await withScale.Content.ReadAsStringAsync());
        (await ReadFieldAsync(type, "Price")).Scale.Should().Be(8);
    }
}
