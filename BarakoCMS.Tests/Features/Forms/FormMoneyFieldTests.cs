using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Forms;

/// <summary>
/// A money field on a public form. The definition tells a widget the currency and the decimal
/// places, and a submission may send the amount as the text an input holds.
/// </summary>
/// <remarks>
/// Every test sends from its own client IP, for the reason <see cref="FormSubmissionTests"/> does.
/// </remarks>
[Collection("Sequential")]
public class FormMoneyFieldTests
{
    private readonly IntegrationTestFixture _factory;

    public FormMoneyFieldTests(IntegrationTestFixture factory) => _factory = factory;

    private HttpClient Visitor()
    {
        var client = _factory.CreateClient();
        var bytes = Guid.NewGuid().ToByteArray();
        var ip = $"2001:db8::{bytes[0]:x2}{bytes[1]:x2}:{bytes[2]:x2}{bytes[3]:x2}:{bytes[4]:x2}{bytes[5]:x2}";
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, ip);
        return client;
    }

    /// <summary>A donation form: an amount in USD, and a tip with no currency declared.</summary>
    private async Task<string> CreateFormAsync()
    {
        var name = $"form-{Guid.NewGuid():N}";

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(),
                Name = name,
                DisplayName = "Donation",
                Fields =
                [
                    new FieldDefinition { Name = "name", DisplayName = "Name", Type = "string", IsRequired = true },
                    new FieldDefinition { Name = "amount", DisplayName = "Amount", Type = "money", Currency = "USD" },
                    new FieldDefinition { Name = "tip", DisplayName = "Tip", Type = "money" },
                ],
            });
            await session.SaveChangesAsync();
        }

        var admin = _factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await _factory.StoredUserTokenAsync("Admin"));
        var enabled = await admin.PutAsJsonAsync($"/api/forms/{name}", new { enabled = true });
        enabled.StatusCode.Should().Be(HttpStatusCode.OK, await enabled.Content.ReadAsStringAsync());

        return name;
    }

    private async Task<IReadOnlyList<Content>> EntriesAsync(string type)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IQuerySession>()
            .Query<Content>().Where(c => c.ContentType == type).ToListAsync();
    }

    [Fact]
    public async Task The_definition_carries_a_money_fields_currency_and_its_decimal_places()
    {
        var type = await CreateFormAsync();

        var response = await Visitor().GetAsync($"/api/public/forms/{type}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var fields = body.RootElement.GetProperty("fields").EnumerateArray()
            .ToDictionary(f => f.GetProperty("name").GetString()!, f => f);
        fields.Should().HaveCount(3);

        fields["amount"].GetProperty("currency").GetString().Should().Be("USD");
        fields["amount"].GetProperty("scale").GetInt32().Should().Be(2, "the field declares none, so it is the currency's own");

        fields["tip"].GetProperty("currency").ValueKind.Should().Be(JsonValueKind.Null);
        fields["tip"].GetProperty("scale").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task An_amount_sent_as_the_text_an_input_holds_is_stored_as_a_number()
    {
        var type = await CreateFormAsync();

        var response = await Visitor().PostAsJsonAsync($"/api/public/forms/{type}",
            new { data = new { name = "Ana", amount = "12.50" } });

        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());

        var entries = await EntriesAsync(type);
        entries.Should().HaveCount(1);
        entries[0].Data["amount"].Should().BeOfType<decimal>();
        entries[0].Data["amount"].Should().Be(12.50m);
    }

    [Fact]
    public async Task An_amount_that_does_not_fit_the_currency_is_refused_as_text_and_as_a_number()
    {
        var type = await CreateFormAsync();

        var asText = await Visitor().PostAsJsonAsync($"/api/public/forms/{type}",
            new { data = new { name = "Ana", amount = "12.345" } });
        asText.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var asNumber = await Visitor().PostAsJsonAsync($"/api/public/forms/{type}",
            new { data = new { name = "Ben", amount = 12.345 } });
        asNumber.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var withSeparator = await Visitor().PostAsJsonAsync($"/api/public/forms/{type}",
            new { data = new { name = "Cy", amount = "1,250.00" } });
        withSeparator.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var fits = await Visitor().PostAsJsonAsync($"/api/public/forms/{type}",
            new { data = new { name = "Di", amount = 12.5 } });
        fits.StatusCode.Should().Be(HttpStatusCode.Accepted, await fits.Content.ReadAsStringAsync());

        var entries = await EntriesAsync(type);
        entries.Should().HaveCount(1, "only the amount that fits was stored");
        entries[0].Data["name"].Should().Be("Di");
    }

    [Fact]
    public async Task A_money_field_with_no_currency_keeps_the_text_it_was_sent_as_it_did()
    {
        var type = await CreateFormAsync();

        var response = await Visitor().PostAsJsonAsync($"/api/public/forms/{type}",
            new { data = new { name = "Ana", tip = "3.999" } });

        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());

        var entries = await EntriesAsync(type);
        entries.Should().HaveCount(1);
        entries[0].Data["tip"].Should().Be("3.999");
    }
}
