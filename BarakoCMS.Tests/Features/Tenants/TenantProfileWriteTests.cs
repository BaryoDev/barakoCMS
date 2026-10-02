using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Models;

// The obsolete Tenant members are what these tests are about: they store them the way an earlier
// release did, and read them back to show what a write left alone.
#pragma warning disable CS0618

namespace BarakoCMS.Tests.Features.Tenants;

/// <summary>
/// The tenant API keeps to routing and administration: it refuses a profile value, leaves a stored
/// one alone, and no longer answers with one (#885).
/// </summary>
[Collection("Sequential")]
public class TenantProfileWriteTests
{
    private static readonly string[] ProfileFields =
        ["logoUrl", "about", "location", "locationUrl", "socialHandle", "email", "contactUrl"];

    private readonly IntegrationTestFixture _fixture;

    public TenantProfileWriteTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<HttpClient> SuperAdminAsync()
    {
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await _fixture.StoredUserTokenAsync("SuperAdmin"));
        // Its own address, so a 429 from a bucket shared with other classes cannot stand in for a 400.
        client.DefaultRequestHeaders.Add(
            TestRemoteIpFilter.Header, $"10.89.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(1, 250)}");
        return client;
    }

    private static string Handle() => $"tpw-{Guid.NewGuid():n}"[..16];

    private async Task<Tenant?> FindAsync(string handle)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return await session.Query<Tenant>().SingleOrDefaultAsync(t => t.Slug == handle, Ct);
    }

    [Theory]
    [InlineData("logoUrl", "https://acme.example/logo.png", "Logo")]
    [InlineData("about", "We make everything.", "About")]
    [InlineData("location", "Koronadal", "Location")]
    [InlineData("locationUrl", "https://maps.example/acme", "LocationUrl")]
    [InlineData("socialHandle", "@acme", "SocialHandle")]
    [InlineData("email", "hello@acme.example", "Email")]
    [InlineData("contactUrl", "https://acme.example/contact", "ContactUrl")]
    public async Task Creating_a_tenant_with_a_profile_field_is_refused_and_names_the_site_field(
        string field, string value, string siteField)
    {
        var client = await SuperAdminAsync();
        var handle = Handle();
        var body = new Dictionary<string, object> { ["handle"] = handle, ["name"] = handle, ["isActive"] = true, [field] = value };

        var created = await client.PostAsJsonAsync("/api/tenants", body, Ct);

        var text = await created.Content.ReadAsStringAsync(Ct);
        created.StatusCode.Should().Be(HttpStatusCode.BadRequest, text);
        text.Should().Contain($"Set the {siteField} field of its site entry", "the refusal says where the value goes now");
        (await FindAsync(handle)).Should().BeNull("a refused create stores no tenant");
    }

    [Fact]
    public async Task Updating_a_tenant_with_a_profile_field_is_refused_and_changes_nothing()
    {
        var client = await SuperAdminAsync();
        var handle = Handle();
        (await client.PostAsJsonAsync("/api/tenants", new { handle, name = "Before", isActive = true }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var updated = await client.PutAsJsonAsync(
            $"/api/tenants/{handle}", new { name = "After", isActive = true, about = "We make everything." }, Ct);

        var text = await updated.Content.ReadAsStringAsync(Ct);
        updated.StatusCode.Should().Be(HttpStatusCode.BadRequest, text);
        text.Should().Contain("Set the About field of its site entry");
        var stored = await FindAsync(handle);
        stored.Should().NotBeNull();
        stored!.Name.Should().Be("Before", "nothing of a refused update is applied");
        stored.About.Should().BeNull();
    }

    /// <summary>
    /// The console saves a tenant's domains by sending the whole tenant back, profile fields
    /// included, and they are empty once the answer stops carrying them.
    /// </summary>
    [Fact]
    public async Task A_blank_or_absent_profile_field_is_accepted()
    {
        var client = await SuperAdminAsync();
        var handle = Handle();
        (await client.PostAsJsonAsync("/api/tenants", new { handle, name = handle, isActive = true, about = (string?)null }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var updated = await client.PutAsJsonAsync(
            $"/api/tenants/{handle}",
            new { name = "Renamed", isActive = true, logoUrl = (string?)null, about = "", email = "   " },
            Ct);

        updated.StatusCode.Should().Be(HttpStatusCode.OK, await updated.Content.ReadAsStringAsync(Ct));
        (await FindAsync(handle))!.Name.Should().Be("Renamed");
    }

    [Fact]
    public async Task An_update_that_leaves_the_profile_out_keeps_what_the_tenant_record_holds()
    {
        var slug = await TenantProfileSeed.TenantAsync(_fixture, Ct, t =>
        {
            t.About = "Kept about";
            t.ContactUrl = "https://kept.example/contact";
        });
        var client = await SuperAdminAsync();

        var updated = await client.PutAsJsonAsync($"/api/tenants/{slug}", new { name = "Renamed", isActive = true }, Ct);

        updated.StatusCode.Should().Be(HttpStatusCode.OK, await updated.Content.ReadAsStringAsync(Ct));
        var stored = await FindAsync(slug);
        stored!.Name.Should().Be("Renamed");
        stored.About.Should().Be("Kept about", "a value not yet moved is still what the profile route falls back to");
        stored.ContactUrl.Should().Be("https://kept.example/contact");
    }

    [Fact]
    public async Task A_tenant_answer_carries_no_profile_fields()
    {
        var slug = await TenantProfileSeed.TenantAsync(_fixture, Ct, t => t.About = "On the record");
        var client = await SuperAdminAsync();

        var updated = await client.PutAsJsonAsync($"/api/tenants/{slug}", new { name = "Renamed", isActive = true }, Ct);

        updated.StatusCode.Should().Be(HttpStatusCode.OK, await updated.Content.ReadAsStringAsync(Ct));
        var answer = await updated.Content.ReadFromJsonAsync<JsonElement>(Ct);
        answer.GetProperty("slug").GetString().Should().Be(slug);
        answer.TryGetProperty("domains", out _).Should().BeTrue("what routing needs is still there");
        answer.TryGetProperty("branding", out _).Should().BeTrue("branding has no new home, so it is still returned");
        ProfileFields.Should().HaveCount(7);
        foreach (var field in ProfileFields)
        {
            answer.TryGetProperty(field, out _).Should().BeFalse("{0} is read from the site entry now", field);
        }
    }
}
