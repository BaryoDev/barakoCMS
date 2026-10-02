using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Models;

// The obsolete Tenant members are stored here the way an earlier release left them, so the old
// value and the site entry's value can be told apart in an answer.
#pragma warning disable CS0618

namespace BarakoCMS.Tests.Features.Tenants;

/// <summary>
/// A tenant's public profile is read from its published site entry, and from the tenant record only
/// where the entry does not hold the field (#885).
/// </summary>
/// <remarks>
/// Each test stores different values on the two sides, so an answer says which side it came from.
/// </remarks>
[Collection("Sequential")]
public class TenantProfileReadTests
{
    private readonly IntegrationTestFixture _fixture;

    public TenantProfileReadTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void OldProfile(Tenant t)
    {
        t.LogoUrl = "https://old.example/logo.png";
        t.About = "Old about";
        t.Location = "Old town";
        t.LocationUrl = "https://old.example/map";
        t.SocialHandle = "@old";
        t.Email = "old@old.example";
        t.ContactUrl = "https://old.example/contact";
    }

    private static Dictionary<string, object> NewProfile() => new()
    {
        ["Name"] = "Site name",
        ["Logo"] = "https://new.example/logo.png",
        ["About"] = "New about",
        ["Location"] = "New town",
        ["LocationUrl"] = "https://new.example/map",
        ["SocialHandle"] = "@new",
        ["Email"] = "new@new.example",
        ["ContactUrl"] = "https://new.example/contact",
    };

    private async Task<HttpClient> PublishedSiteAsync(string slug, Dictionary<string, object> data)
    {
        var admin = await TenantProfileSeed.AdminInAsync(_fixture, slug, Ct);
        await TenantProfileSeed.ApplySiteBlueprintAsync(admin, Ct);
        var id = await TenantProfileSeed.CreateSiteEntryAsync(admin, data, Ct);
        await TenantProfileSeed.PublishAsync(admin, id, Ct);
        return admin;
    }

    [Fact]
    public async Task The_public_profile_answers_from_the_published_site_entry()
    {
        var slug = await TenantProfileSeed.TenantAsync(_fixture, Ct, OldProfile);
        await PublishedSiteAsync(slug, NewProfile());

        var profile = await TenantProfileSeed.PublicProfileAsync(_fixture, slug, Ct);

        TenantProfileSeed.Field(profile, "handle").Should().Be(slug);
        TenantProfileSeed.Field(profile, "name").Should().Be($"Name of {slug}", "the name is still the tenant's own");
        TenantProfileSeed.Field(profile, "logoUrl").Should().Be("https://new.example/logo.png");
        TenantProfileSeed.Field(profile, "about").Should().Be("New about");
        TenantProfileSeed.Field(profile, "location").Should().Be("New town");
        TenantProfileSeed.Field(profile, "locationUrl").Should().Be("https://new.example/map");
        TenantProfileSeed.Field(profile, "socialHandle").Should().Be("@new");
        TenantProfileSeed.Field(profile, "email").Should().Be("new@new.example");
        TenantProfileSeed.Field(profile, "contactUrl").Should().Be("https://new.example/contact");
    }

    [Fact]
    public async Task A_field_the_site_entry_leaves_out_answers_from_the_tenant_record()
    {
        var slug = await TenantProfileSeed.TenantAsync(_fixture, Ct, OldProfile);
        await PublishedSiteAsync(slug, new Dictionary<string, object> { ["Name"] = "Site name", ["About"] = "New about", ["Email"] = "  " });

        var profile = await TenantProfileSeed.PublicProfileAsync(_fixture, slug, Ct);

        TenantProfileSeed.Field(profile, "about").Should().Be("New about");
        TenantProfileSeed.Field(profile, "location").Should().Be("Old town");
        TenantProfileSeed.Field(profile, "email").Should().Be("old@old.example", "a blank in the entry is not a value");
        TenantProfileSeed.Field(profile, "logoUrl").Should().Be("https://old.example/logo.png");
    }

    [Fact]
    public async Task A_tenant_with_no_site_entry_answers_from_the_tenant_record()
    {
        var slug = await TenantProfileSeed.TenantAsync(_fixture, Ct, OldProfile);

        var profile = await TenantProfileSeed.PublicProfileAsync(_fixture, slug, Ct);

        TenantProfileSeed.Field(profile, "about").Should().Be("Old about");
        TenantProfileSeed.Field(profile, "contactUrl").Should().Be("https://old.example/contact");
        TenantProfileSeed.Field(profile, "socialHandle").Should().Be("@old");
    }

    [Fact]
    public async Task A_draft_site_entry_is_not_read()
    {
        var slug = await TenantProfileSeed.TenantAsync(_fixture, Ct, OldProfile);
        var admin = await TenantProfileSeed.AdminInAsync(_fixture, slug, Ct);
        await TenantProfileSeed.ApplySiteBlueprintAsync(admin, Ct);
        await TenantProfileSeed.CreateSiteEntryAsync(admin, NewProfile(), Ct);

        var profile = await TenantProfileSeed.PublicProfileAsync(_fixture, slug, Ct);

        TenantProfileSeed.Field(profile, "about").Should().Be("Old about", "an unpublished entry is not delivered anywhere");
        TenantProfileSeed.Field(profile, "email").Should().Be("old@old.example");
    }

    [Fact]
    public async Task A_site_field_that_is_not_public_is_not_served()
    {
        var slug = await TenantProfileSeed.TenantAsync(_fixture, Ct);
        await PublishedSiteAsync(slug, NewProfile());

        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using (var session = store.LightweightSession(slug))
        {
            var site = await session.Query<ContentTypeDefinition>().SingleAsync(d => d.Name == "site", Ct);
            site.Fields.Single(f => f.Name == "Email").Sensitivity = SensitivityLevel.Sensitive;
            session.Store(site);
            await session.SaveChangesAsync(Ct);
        }

        var profile = await TenantProfileSeed.PublicProfileAsync(_fixture, slug, Ct);

        TenantProfileSeed.Field(profile, "about").Should().Be("New about", "the entry is being read");
        TenantProfileSeed.Field(profile, "email").Should().BeNull("delivery does not serve a field the type marks Sensitive");
    }

    [Fact]
    public async Task An_inactive_tenant_still_has_no_public_profile()
    {
        var slug = await TenantProfileSeed.TenantAsync(_fixture, Ct, t =>
        {
            OldProfile(t);
            t.IsActive = false;
        });

        var response = await TenantProfileSeed.Anonymous(_fixture).GetAsync($"/api/tenants/{slug}/public", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task My_tenants_answers_with_the_logo_of_the_site_entry()
    {
        var slug = await TenantProfileSeed.TenantAsync(_fixture, Ct, OldProfile);
        var member = await PublishedSiteAsync(slug, NewProfile());

        var response = await member.GetAsync("/api/me/tenants", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var mine = body.GetProperty("items").EnumerateArray()
            .Where(t => t.GetProperty("slug").GetString() == slug)
            .ToList();
        mine.Should().HaveCount(1, "the caller's one membership is in this tenant");
        mine[0].GetProperty("name").GetString().Should().Be($"Name of {slug}");
        mine[0].GetProperty("logoUrl").GetString().Should().Be("https://new.example/logo.png");
        mine[0].TryGetProperty("branding", out _).Should().BeTrue();
    }
}
