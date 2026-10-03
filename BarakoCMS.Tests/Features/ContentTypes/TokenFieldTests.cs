using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using barakoCMS.Models;
using Xunit;
using Probe = BarakoCMS.Tests.TokenFieldProbe;

namespace BarakoCMS.Tests.Features.ContentTypes;

/// <summary>
/// A token field over HTTP: filled when an entry is created, whatever the caller sent, and read
/// only by a caller who may read a field of its level. Then the type endpoints: a token field
/// with no sensitivity is stored Hidden, and nothing makes it Public.
/// </summary>
[Collection("Sequential")]
public class TokenFieldTests
{
    private readonly IntegrationTestFixture _factory;

    public TokenFieldTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<string> TypeAsync(params FieldDefinition[] extra) =>
        Probe.StoreTypeAsync(_factory, "tok", false, [Probe.Text("Name"), .. extra]);

    private async Task<(HttpStatusCode Status, string Body)> GetEntryAsync(string token, Guid id)
    {
        var response = await Probe.ClientFor(_factory, token).GetAsync($"/api/contents/{id}", Ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Creating_an_entry_fills_the_token_with_32_characters_of_the_alphabet()
    {
        var type = await TypeAsync(Probe.Token());

        var id = await Probe.CreateAsync(_factory, await Probe.AdminAsync(_factory), type, new() { ["Name"] = "Ana" });

        var token = Probe.TokenOf(await Probe.StoredAsync(_factory, id));
        Probe.ShouldBeAToken(token);
    }

    [Fact]
    public async Task A_token_sent_on_create_is_discarded_in_every_casing_and_another_is_generated()
    {
        const string sent = "chosen0by0the0caller0000000000000";
        var type = await TypeAsync(Probe.Token());

        var id = await Probe.CreateAsync(_factory, await Probe.AdminAsync(_factory), type, new()
        {
            ["Name"] = "Ana",
            ["ClaimToken"] = sent,
            ["claimtoken"] = sent,
        });

        var stored = await Probe.StoredAsync(_factory, id);
        var token = Probe.TokenOf(stored);
        token.Should().NotBe(sent, "the caller who may read every field still may not choose this one");
        Probe.ShouldBeAToken(token);
        stored.Data.Keys.Where(k => k.Equals("ClaimToken", StringComparison.OrdinalIgnoreCase))
            .Should().HaveCount(1, "the sent casing is not kept beside the generated value");
    }

    [Fact]
    public async Task Entries_of_a_type_each_get_a_token_of_their_own()
    {
        var type = await TypeAsync(Probe.Token());
        var admin = await Probe.AdminAsync(_factory);

        var tokens = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            var id = await Probe.CreateAsync(_factory, admin, type, new() { ["Name"] = $"Entry {i}" });
            tokens.Add(Probe.TokenOf(await Probe.StoredAsync(_factory, id)));
        }

        tokens.Should().HaveCount(5);
        tokens.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task A_declared_length_is_the_length_of_the_token()
    {
        var type = await TypeAsync(Probe.Token(length: 16));

        var id = await Probe.CreateAsync(_factory, await Probe.AdminAsync(_factory), type, new() { ["Name"] = "Ana" });

        Probe.ShouldBeAToken(Probe.TokenOf(await Probe.StoredAsync(_factory, id)), length: 16);
    }

    [Fact]
    public async Task A_caller_without_view_hidden_is_not_shown_the_token_and_one_holding_it_is()
    {
        var type = await TypeAsync(Probe.Token());
        var viewer = await Probe.UserAsync(_factory, $"Viewer_{Guid.NewGuid():N}", type);
        var holder = await Probe.UserAsync(_factory, SystemCapabilities.ViewHidden, type);

        // Created by the caller who may not read it: they still get an entry with a token.
        var id = await Probe.CreateAsync(_factory, viewer, type, new() { ["Name"] = "Ana" });
        var token = Probe.TokenOf(await Probe.StoredAsync(_factory, id));

        var (viewerStatus, viewerBody) = await GetEntryAsync(viewer, id);
        viewerStatus.Should().Be(HttpStatusCode.OK, viewerBody);
        var seen = JsonDocument.Parse(viewerBody).RootElement.GetProperty("data");
        seen.TryGetProperty("Name", out _).Should().BeTrue("the entry is readable, so the absence below is the field rule");
        seen.TryGetProperty("ClaimToken", out _).Should().BeFalse();
        viewerBody.Should().NotContain(token);

        var (holderStatus, holderBody) = await GetEntryAsync(holder, id);
        holderStatus.Should().Be(HttpStatusCode.OK, holderBody);
        JsonDocument.Parse(holderBody).RootElement.GetProperty("data").GetProperty("ClaimToken").GetString()
            .Should().Be(token);
    }

    [Fact]
    public async Task A_token_field_created_with_no_sensitivity_is_stored_hidden()
    {
        var name = "tokdef" + Guid.NewGuid().ToString("n")[..10];
        var admin = Probe.ClientFor(_factory, await Probe.AdminAsync(_factory));

        var created = await admin.PostAsJsonAsync("/api/content-types", new
        {
            name,
            displayName = "Ticket",
            fields = new object[]
            {
                new { name = "Name", displayName = "Name", type = "string" },
                new { name = "ClaimToken", displayName = "Claim token", type = "token", tokenLength = 24 },
            },
        }, Ct);
        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync(Ct));

        var stored = (await Probe.StoredTypeAsync(_factory, name)).Fields.Single(f => f.Name == "ClaimToken");
        stored.Type.Should().Be("token");
        stored.Sensitivity.Should().Be(SensitivityLevel.Hidden);
        stored.TokenLength.Should().Be(24);
    }

    [Fact]
    public async Task Adding_a_token_field_with_no_sensitivity_stores_it_hidden()
    {
        var type = await TypeAsync();
        var admin = Probe.ClientFor(_factory, await Probe.AdminAsync(_factory));

        var added = await admin.PostAsJsonAsync($"/api/content-types/{type}/fields", new
        {
            fieldName = "ClaimToken",
            displayName = "Claim token",
            type = "token",
            tokenLength = 20,
        }, Ct);
        added.StatusCode.Should().Be(HttpStatusCode.OK, await added.Content.ReadAsStringAsync(Ct));

        var stored = (await Probe.StoredTypeAsync(_factory, type)).Fields.Single(f => f.Name == "ClaimToken");
        stored.Sensitivity.Should().Be(SensitivityLevel.Hidden);
        stored.TokenLength.Should().Be(20);
    }

    [Fact]
    public async Task A_token_field_cannot_be_made_public()
    {
        var type = await TypeAsync(Probe.Token());
        var admin = Probe.ClientFor(_factory, await Probe.AdminAsync(_factory));

        var lowered = await admin.PutAsJsonAsync(
            $"/api/content-types/{type}/fields/ClaimToken/sensitivity",
            new { sensitivity = "Public", acknowledgeDisclosure = true },
            Ct);

        lowered.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await lowered.Content.ReadAsStringAsync(Ct)).Should().Contain("is a token");
        (await Probe.StoredTypeAsync(_factory, type)).Fields.Single(f => f.Name == "ClaimToken")
            .Sensitivity.Should().Be(SensitivityLevel.Hidden);
    }

    [Fact]
    public async Task An_event_sourced_type_cannot_have_a_token_field()
    {
        var name = "tokes" + Guid.NewGuid().ToString("n")[..10];
        var admin = Probe.ClientFor(_factory, await Probe.AdminAsync(_factory));

        var created = await admin.PostAsJsonAsync("/api/content-types", new
        {
            name,
            displayName = "Ticket",
            eventSourced = true,
            fields = new object[]
            {
                new { name = "Name", displayName = "Name", type = "string" },
                new { name = "ClaimToken", displayName = "Claim token", type = "token" },
            },
        }, Ct);

        created.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await created.Content.ReadAsStringAsync(Ct)).Should().Contain("ClaimToken");
    }
}
