using System.Net;
using System.Text.Json;
using FluentAssertions;
using barakoCMS.Models;
using Xunit;
using Probe = BarakoCMS.Tests.TokenFieldProbe;

namespace BarakoCMS.Tests.Features.ContentApi;

/// <summary>
/// The entries list against a token: <c>search</c> never matches one, for any caller, and a field
/// filter on one works only for a caller who may read it.
/// </summary>
/// <remarks>
/// The search matches substrings, so a token it matched could be read off one character at a
/// time by a caller who is never shown it. Each test finds its own entries by type, and checks
/// the search still finds the entry by another field, so an empty answer means the token was
/// skipped and not that the search is broken.
/// </remarks>
[Collection("Sequential")]
public class TokenFieldSearchTests
{
    private readonly IntegrationTestFixture _factory;

    public TokenFieldSearchTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(string token, string query)
    {
        var response = await Probe.ClientFor(_factory, token).GetAsync("/api/contents?" + query, Ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    private async Task<List<Guid>> IdsAsync(string token, string query)
    {
        var (status, body) = await SendAsync(token, query);
        status.Should().Be(HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("id").GetGuid())
            .ToList();
    }

    [Fact]
    public async Task Search_does_not_match_a_token_or_part_of_one_even_for_a_caller_who_may_read_it()
    {
        var type = await Probe.StoreTypeAsync(_factory, "toksearch", false, Probe.Text("Name"), Probe.Token());
        var admin = await Probe.AdminAsync(_factory);
        var id = await Probe.CreateAsync(_factory, admin, type, new() { ["Name"] = "Ana" });
        var token = Probe.TokenOf(await Probe.StoredAsync(_factory, id));

        var byName = await IdsAsync(admin, $"contentType={type}&search=Ana&pageSize=100");
        byName.Should().HaveCount(1, "the search works on the entry's other fields");
        byName.Should().Equal(id);

        (await IdsAsync(admin, $"contentType={type}&search={token}&pageSize=100")).Should().BeEmpty();
        (await IdsAsync(admin, $"contentType={type}&search={token[..6]}&pageSize=100")).Should().BeEmpty();
        (await IdsAsync(admin, $"search={token}&pageSize=100")).Should().NotContain(id,
            "across every type as well as within one");
    }

    [Fact]
    public async Task A_filter_on_the_token_finds_the_entry_for_a_caller_who_may_read_it_and_is_refused_for_one_who_may_not()
    {
        var type = await Probe.StoreTypeAsync(_factory, "tokfilter", false, Probe.Text("Name"), Probe.Token());
        var admin = await Probe.AdminAsync(_factory);
        var viewer = await Probe.UserAsync(_factory, $"Viewer_{Guid.NewGuid():N}", type);

        var id = await Probe.CreateAsync(_factory, admin, type, new() { ["Name"] = "Ana" });
        var other = await Probe.CreateAsync(_factory, admin, type, new() { ["Name"] = "Ben" });
        var token = Probe.TokenOf(await Probe.StoredAsync(_factory, id));

        var found = await IdsAsync(admin, $"contentType={type}&filter[ClaimToken][eq]={token}&pageSize=100");
        found.Should().HaveCount(1);
        found.Should().Equal(id);
        found.Should().NotContain(other);

        var (status, body) = await SendAsync(viewer, $"contentType={type}&filter[ClaimToken][eq]={token}&pageSize=100");
        status.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("is not filterable").And.NotContain(token);
    }
}
