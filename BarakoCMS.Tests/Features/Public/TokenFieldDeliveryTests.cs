using System.Net;
using System.Text.Json;
using FluentAssertions;
using barakoCMS.Models;
using Xunit;
using Probe = BarakoCMS.Tests.TokenFieldProbe;

namespace BarakoCMS.Tests.Features.Public;

/// <summary>
/// Anonymous delivery of a type with a token field leaves the token out, including from a type
/// whose definition was stored with the field Public, which no endpoint does.
/// </summary>
/// <remarks>
/// Each test finds its own entry by id in its own type, and checks a Public field of that entry is
/// there, so a missing token means the field was left out and not that the entry was.
/// </remarks>
[Collection("Sequential")]
public class TokenFieldDeliveryTests
{
    private readonly IntegrationTestFixture _factory;

    public TokenFieldDeliveryTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(JsonElement Item, string Body)> DeliveredAsync(string type, Guid id)
    {
        var response = await _factory.CreateClient().GetAsync($"/api/public/{type}?pageSize=100", Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        var items = JsonDocument.Parse(body).RootElement.GetProperty("items").EnumerateArray()
            .Where(i => i.GetProperty("id").GetGuid() == id)
            .Select(i => i.Clone())
            .ToList();
        items.Should().HaveCount(1, "the entry is published, Public and of a deliverable type");
        return (items[0], body);
    }

    private async Task AssertLeftOutAsync(SensitivityLevel declared)
    {
        var type = await Probe.StoreTypeAsync(
            _factory, "tokpub", deliverable: true, Probe.Text("Title"), Probe.Token(sensitivity: declared));

        var id = await Probe.CreateAsync(
            _factory, await Probe.AdminAsync(_factory), type, new() { ["Title"] = "Open day" }, ContentStatus.Published);
        var token = Probe.TokenOf(await Probe.StoredAsync(_factory, id));

        var (item, body) = await DeliveredAsync(type, id);

        var data = item.GetProperty("data");
        data.GetProperty("Title").GetString().Should().Be("Open day");
        data.TryGetProperty("ClaimToken", out _).Should().BeFalse();
        body.Should().NotContain(token);
    }

    [Fact]
    public async Task Delivery_leaves_out_a_hidden_token()
    {
        await AssertLeftOutAsync(SensitivityLevel.Hidden);
    }

    [Fact]
    public async Task Delivery_leaves_out_a_token_whose_definition_was_stored_public()
    {
        await AssertLeftOutAsync(SensitivityLevel.Public);
    }
}
