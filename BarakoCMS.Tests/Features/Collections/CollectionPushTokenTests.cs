using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Models;
using Xunit;
using Probe = BarakoCMS.Tests.TokenFieldProbe;

namespace BarakoCMS.Tests.Features.Collections;

/// <summary>
/// A push keyed by slug into a type with a token field. The source does not know the tokens, so
/// it leaves them out, and a value it does send is not taken.
/// </summary>
/// <remarks>
/// The pusher is the seeded SuperAdmin, who may see every field. The stored token has to be put
/// back before the push compares an entry with what is stored, or every push of an unchanged entry
/// would count as a change and append an event.
/// </remarks>
[Collection("Sequential")]
public class CollectionPushTokenTests
{
    private readonly IntegrationTestFixture _factory;

    public CollectionPushTokenTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<string> TypeAsync() => Probe.StoreTypeAsync(
        _factory, "tokpush", false,
        new FieldDefinition { Name = "slug", Type = "slug", IsRequired = true },
        new FieldDefinition { Name = "title", Type = "string" },
        Probe.Token("claim"));

    private async Task<JsonElement> PushOkAsync(string type, params Dictionary<string, object>[] entries)
    {
        var client = Probe.ClientFor(_factory, await Probe.AdminAsync(_factory));
        var response = await client.PostAsJsonAsync($"/api/collections/{type}/push", new { entries }, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<Content> OnlyEntryAsync(string type)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var entries = await session.Query<Content>().Where(c => c.ContentType == type).ToListAsync(Ct);
        entries.Should().HaveCount(1);
        return entries[0];
    }

    [Fact]
    public async Task Pushing_an_unchanged_entry_again_leaves_it_unchanged_and_keeps_its_token()
    {
        var type = await TypeAsync();

        var first = await PushOkAsync(type, new Dictionary<string, object> { ["slug"] = "ticket-1", ["title"] = "Ticket 1" });
        first.GetProperty("created").GetInt32().Should().Be(1);
        var token = Probe.TokenOf(await OnlyEntryAsync(type), "claim");

        var second = await PushOkAsync(type, new Dictionary<string, object> { ["slug"] = "ticket-1", ["title"] = "Ticket 1" });

        second.GetProperty("unchanged").GetInt32().Should().Be(1, "the pushed entry is the stored one with its token");
        second.GetProperty("updated").GetInt32().Should().Be(0);
        Probe.TokenOf(await OnlyEntryAsync(type), "claim").Should().Be(token);
    }

    [Fact]
    public async Task A_push_that_sends_a_token_does_not_change_the_stored_one()
    {
        var type = await TypeAsync();

        await PushOkAsync(type, new Dictionary<string, object> { ["slug"] = "ticket-1", ["title"] = "Ticket 1" });
        var token = Probe.TokenOf(await OnlyEntryAsync(type), "claim");

        var forged = await PushOkAsync(type, new Dictionary<string, object>
        {
            ["slug"] = "ticket-1",
            ["title"] = "Ticket 1",
            ["claim"] = "forged0token0forged0token0forged",
        });

        forged.GetProperty("unchanged").GetInt32().Should().Be(1, "the sent token is dropped before the comparison");
        Probe.TokenOf(await OnlyEntryAsync(type), "claim").Should().Be(token);
    }
}
