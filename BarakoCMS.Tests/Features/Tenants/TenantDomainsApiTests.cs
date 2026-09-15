using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests.Features.Tenants;

/// <summary>
/// A tenant's domains are set through the API, and a renderer can ask which tenant a domain belongs to.
/// </summary>
/// <remarks>
/// Before this, <c>Tenant.Domains</c> was returned by the API and writable only in the database, and
/// the domain map was cached for five minutes with nothing invalidating it. One renderer serving
/// several sites resolves each request's host to a tenant, so an operator has to be able to add a
/// domain and see it take effect on the next request.
/// </remarks>
[Collection("Sequential")]
public class TenantDomainsApiTests
{
    private readonly IntegrationTestFixture _fixture;

    public TenantDomainsApiTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<HttpClient> SuperAdminAsync()
    {
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await _fixture.StoredUserTokenAsync("SuperAdmin"));
        return client;
    }

    private static string Handle() => $"dom-{Guid.NewGuid():n}"[..16];

    private static string Domain() => $"site-{Guid.NewGuid():n}"[..17] + ".example";

    private static Task<HttpResponseMessage> CreateAsync(HttpClient client, string handle, params string[] domains) =>
        client.PostAsJsonAsync("/api/tenants", new { handle, name = handle, isActive = true, domains }, Ct);

    private static Task<HttpResponseMessage> UpdateAsync(HttpClient client, string handle, object body) =>
        client.PutAsJsonAsync($"/api/tenants/{handle}", body, Ct);

    private async Task<HttpResponseMessage> ByHostAsync(string host) =>
        await _fixture.CreateClient().GetAsync($"/api/tenants/by-host/{host}", Ct);

    private static async Task<List<string>> DomainsOf(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return body.RootElement.GetProperty("domains").EnumerateArray().Select(d => d.GetString()!).ToList();
    }

    [Fact]
    public async Task A_domain_given_on_create_is_stored_as_the_bare_host_and_resolves()
    {
        var client = await SuperAdminAsync();
        var handle = Handle();
        var domain = Domain();

        var created = await CreateAsync(client, handle, $"  WWW.{domain.ToUpperInvariant()}.  ");

        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync(Ct));
        (await DomainsOf(created)).Should().Equal(domain);

        foreach (var host in new[] { domain, $"www.{domain}" })
        {
            var found = await ByHostAsync(host);
            found.StatusCode.Should().Be(HttpStatusCode.OK, "{0} was registered", host);
            (await found.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("handle").GetString().Should().Be(handle);
        }
    }

    /// <summary>
    /// The map is cached for five minutes. Looking the domain up before the update is what fills the
    /// cache with an answer that does not include it, so a lookup that still finds it afterwards
    /// proves the write invalidated the cache rather than the cache being empty.
    /// </summary>
    [Fact]
    public async Task A_domain_added_on_update_is_found_by_the_very_next_lookup()
    {
        var client = await SuperAdminAsync();
        var handle = Handle();
        var domain = Domain();
        (await CreateAsync(client, handle)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await ByHostAsync(domain)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var updated = await UpdateAsync(client, handle, new { name = handle, isActive = true, domains = new[] { domain } });
        updated.StatusCode.Should().Be(HttpStatusCode.OK, await updated.Content.ReadAsStringAsync(Ct));

        (await ByHostAsync(domain)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_update_without_domains_keeps_them_and_an_empty_list_clears_them()
    {
        var client = await SuperAdminAsync();
        var handle = Handle();
        var domain = Domain();
        (await CreateAsync(client, handle, domain)).StatusCode.Should().Be(HttpStatusCode.OK);

        var renamed = await UpdateAsync(client, handle, new { name = "Renamed", isActive = true });
        renamed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await DomainsOf(renamed)).Should().Equal(domain);
        (await ByHostAsync(domain)).StatusCode.Should().Be(HttpStatusCode.OK);

        var cleared = await UpdateAsync(client, handle, new { name = "Renamed", isActive = true, domains = Array.Empty<string>() });
        cleared.StatusCode.Should().Be(HttpStatusCode.OK);
        (await DomainsOf(cleared)).Should().BeEmpty();
        (await ByHostAsync(domain)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_domain_another_tenant_holds_is_refused_and_names_that_tenant()
    {
        var client = await SuperAdminAsync();
        var holder = Handle();
        var other = Handle();
        var domain = Domain();
        (await CreateAsync(client, holder, domain)).StatusCode.Should().Be(HttpStatusCode.OK);

        var onCreate = await CreateAsync(client, other, domain);
        onCreate.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await onCreate.Content.ReadAsStringAsync(Ct)).Should().Contain(holder);

        (await CreateAsync(client, other)).StatusCode.Should().Be(HttpStatusCode.OK);
        var onUpdate = await UpdateAsync(client, other, new { name = other, isActive = true, domains = new[] { $"www.{domain}" } });
        onUpdate.StatusCode.Should().Be(HttpStatusCode.Conflict, "www is the same site, so it is the same domain");

        var found = await ByHostAsync(domain);
        (await found.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("handle").GetString().Should().Be(holder);
    }

    [Fact]
    public async Task A_tenant_can_keep_its_own_domain_when_it_saves_again()
    {
        var client = await SuperAdminAsync();
        var handle = Handle();
        var domain = Domain();
        (await CreateAsync(client, handle, domain)).StatusCode.Should().Be(HttpStatusCode.OK);

        var saved = await UpdateAsync(client, handle, new { name = handle, isActive = true, domains = new[] { domain } });

        saved.StatusCode.Should().Be(HttpStatusCode.OK, "a tenant does not clash with itself");
    }

    [Theory]
    [InlineData("https://shop.example")]
    [InlineData("shop.example:8080")]
    [InlineData("shop.example/path")]
    [InlineData("*.shop.example")]
    [InlineData("localhost")]
    [InlineData("192.168.1.10")]
    [InlineData("bad_label.example")]
    [InlineData("")]
    public async Task A_value_that_is_not_a_bare_domain_is_refused(string value)
    {
        var client = await SuperAdminAsync();

        var response = await CreateAsync(client, Handle(), value);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_lookup_answers_only_the_handle_and_skips_unknown_and_inactive_tenants()
    {
        var client = await SuperAdminAsync();
        var active = Handle();
        var paused = Handle();
        var activeDomain = Domain();
        var pausedDomain = Domain();
        (await CreateAsync(client, active, activeDomain)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await CreateAsync(client, paused, pausedDomain)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await UpdateAsync(client, paused, new { name = paused, isActive = false })).StatusCode.Should().Be(HttpStatusCode.OK);

        var found = await ByHostAsync(activeDomain);
        found.StatusCode.Should().Be(HttpStatusCode.OK);
        var properties = (await found.Content.ReadFromJsonAsync<JsonElement>(Ct)).EnumerateObject().Select(p => p.Name).ToList();
        properties.Should().Equal(new[] { "handle" }, "which tenant a public domain serves is public, nothing else about it is");

        (await ByHostAsync(pausedDomain)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ByHostAsync(Domain())).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// Request routing falls back to the leading subdomain when no domain row matches, so the lookup
    /// has to as well, or a renderer is told a host the API itself serves belongs to nobody.
    /// </summary>
    [Fact]
    public async Task A_subdomain_host_resolves_to_its_active_tenant_without_a_domain_row()
    {
        var client = await SuperAdminAsync();
        var active = Handle();
        var paused = Handle();
        (await CreateAsync(client, active)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await CreateAsync(client, paused)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await UpdateAsync(client, paused, new { name = paused, isActive = false })).StatusCode.Should().Be(HttpStatusCode.OK);

        var found = await ByHostAsync($"{active}.sites.example");
        found.StatusCode.Should().Be(HttpStatusCode.OK, await found.Content.ReadAsStringAsync(Ct));
        var body = await found.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.EnumerateObject().Select(p => p.Name).Should().Equal("handle");
        body.GetProperty("handle").GetString().Should().Be(active);

        (await ByHostAsync($"{paused}.sites.example")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ByHostAsync($"{Handle()}.sites.example")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ByHostAsync("www.sites.example")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
