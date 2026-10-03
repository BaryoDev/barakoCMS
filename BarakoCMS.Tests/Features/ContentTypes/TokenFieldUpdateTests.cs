using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using barakoCMS.Models;
using Xunit;
using Probe = BarakoCMS.Tests.TokenFieldProbe;

namespace BarakoCMS.Tests.Features.ContentTypes;

/// <summary>
/// After an entry exists its token does not change: an update or a rollback puts the stored one
/// back whatever was sent, and an entry written before its type had the field gets one on its next
/// save and keeps it.
/// </summary>
/// <remarks>
/// The caller in each case is the seeded SuperAdmin, who may read and write every other field, so
/// the field-sensitivity rule for callers who may not see a field is not what keeps the token.
/// </remarks>
[Collection("Sequential")]
public class TokenFieldUpdateTests
{
    private readonly IntegrationTestFixture _factory;

    public TokenFieldUpdateTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<string> TypeAsync(params FieldDefinition[] extra) =>
        Probe.StoreTypeAsync(_factory, "tokup", false, [Probe.Text("Name"), .. extra]);

    private async Task UpdateOkAsync(string admin, Guid id, Dictionary<string, object> data)
    {
        var response = await Probe.UpdateAsync(_factory, admin, id, data);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
    }

    private async Task AddTokenFieldAsync(string admin, string type)
    {
        var added = await Probe.ClientFor(_factory, admin).PostAsJsonAsync($"/api/content-types/{type}/fields", new
        {
            fieldName = "ClaimToken",
            displayName = "Claim token",
            type = "token",
        }, Ct);
        added.StatusCode.Should().Be(HttpStatusCode.OK, await added.Content.ReadAsStringAsync(Ct));
    }

    private async Task<List<JsonElement>> HistoryAsync(string admin, Guid id)
    {
        var response = await Probe.ClientFor(_factory, admin).GetAsync($"/api/contents/{id}/history?pageSize=100", Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.GetProperty("items").EnumerateArray().Select(e => e.Clone()).ToList();
    }

    [Fact]
    public async Task An_update_that_sends_another_token_is_accepted_and_the_stored_token_stays()
    {
        var type = await TypeAsync(Probe.Token());
        var admin = await Probe.AdminAsync(_factory);
        var id = await Probe.CreateAsync(_factory, admin, type, new() { ["Name"] = "Ana" });
        var token = Probe.TokenOf(await Probe.StoredAsync(_factory, id));

        await UpdateOkAsync(admin, id, new() { ["Name"] = "Ana Cruz", ["ClaimToken"] = "forged0token0forged0token0forged" });

        var stored = await Probe.StoredAsync(_factory, id);
        stored.Data["Name"].ToString().Should().Be("Ana Cruz", "the rest of the update was applied");
        Probe.TokenOf(stored).Should().Be(token);
    }

    [Fact]
    public async Task An_update_that_leaves_the_token_out_keeps_it()
    {
        var type = await TypeAsync(Probe.Token());
        var admin = await Probe.AdminAsync(_factory);
        var id = await Probe.CreateAsync(_factory, admin, type, new() { ["Name"] = "Ana" });
        var token = Probe.TokenOf(await Probe.StoredAsync(_factory, id));

        await UpdateOkAsync(admin, id, new() { ["Name"] = "Ana Cruz" });

        Probe.TokenOf(await Probe.StoredAsync(_factory, id)).Should().Be(token,
            "an update replaces the data, and leaving the token out must not be a way to remove it");
    }

    [Fact]
    public async Task An_entry_written_before_the_field_existed_gets_a_token_on_its_next_save_and_keeps_it()
    {
        var type = await TypeAsync();
        var admin = await Probe.AdminAsync(_factory);
        var id = await Probe.CreateAsync(_factory, admin, type, new() { ["Name"] = "Ana" });

        await AddTokenFieldAsync(admin, type);
        (await Probe.StoredAsync(_factory, id)).Data.Should().NotContainKey("ClaimToken",
            "adding the field rewrites no entry");

        await UpdateOkAsync(admin, id, new() { ["Name"] = "Ana Cruz" });
        var token = Probe.TokenOf(await Probe.StoredAsync(_factory, id));
        Probe.ShouldBeAToken(token);

        await UpdateOkAsync(admin, id, new() { ["Name"] = "Ana M. Cruz" });
        Probe.TokenOf(await Probe.StoredAsync(_factory, id)).Should().Be(token, "a token is issued once");

        // The stream holds what the document holds, so a rebuild or a rollback reads the same token.
        var updates = (await HistoryAsync(admin, id))
            .Where(v => v.GetProperty("changeType").GetString() == "Updated")
            .ToList();
        updates.Should().HaveCount(2);
        updates.Should().OnlyContain(v => v.GetProperty("data").GetProperty("ClaimToken").GetString() == token);
    }

    [Fact]
    public async Task A_rollback_to_a_version_from_before_the_token_keeps_the_token()
    {
        var type = await TypeAsync();
        var admin = await Probe.AdminAsync(_factory);
        var id = await Probe.CreateAsync(_factory, admin, type, new() { ["Name"] = "Ana" });

        await AddTokenFieldAsync(admin, type);
        await UpdateOkAsync(admin, id, new() { ["Name"] = "Ana Cruz" });
        var token = Probe.TokenOf(await Probe.StoredAsync(_factory, id));

        var created = (await HistoryAsync(admin, id)).Single(v => v.GetProperty("changeType").GetString() == "Created");
        created.GetProperty("data").TryGetProperty("ClaimToken", out _).Should().BeFalse(
            "the version rolled back to has no token, or this proves nothing about putting it back");

        var rollback = await Probe.ClientFor(_factory, admin).PostAsJsonAsync(
            $"/api/contents/{id}/rollback/{created.GetProperty("versionId").GetGuid()}", new { }, Ct);
        rollback.StatusCode.Should().Be(HttpStatusCode.OK, await rollback.Content.ReadAsStringAsync(Ct));

        var stored = await Probe.StoredAsync(_factory, id);
        stored.Data["Name"].ToString().Should().Be("Ana", "the rollback itself happened");
        Probe.TokenOf(stored).Should().Be(token);
    }
}
