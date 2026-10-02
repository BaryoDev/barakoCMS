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

    /// <summary>Publishes an entry of a site type the test stored by hand.</summary>
    private async Task PublishedEntryAsync(string slug, Dictionary<string, object> data)
    {
        var admin = await TenantProfileSeed.AdminInAsync(_fixture, slug, Ct);
        var id = await TenantProfileSeed.CreateSiteEntryAsync(admin, data, Ct);
        await TenantProfileSeed.PublishAsync(admin, id, Ct);
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

    /// <summary>
    /// A key the entry never had is a field nobody set, which is every site made before the move:
    /// its <c>Logo</c> is declared and absent, and the tenant's own logo has to keep answering.
    /// </summary>
    [Fact]
    public async Task A_field_the_site_entry_has_no_key_for_answers_from_the_tenant_record()
    {
        var slug = await TenantProfileSeed.TenantAsync(_fixture, Ct, OldProfile);
        await PublishedSiteAsync(slug, new Dictionary<string, object> { ["Name"] = "Site name", ["About"] = "New about" });

        var profile = await TenantProfileSeed.PublicProfileAsync(_fixture, slug, Ct);

        TenantProfileSeed.Field(profile, "about").Should().Be("New about");
        TenantProfileSeed.Field(profile, "location").Should().Be("Old town");
        TenantProfileSeed.Field(profile, "logoUrl").Should().Be("https://old.example/logo.png");
    }

    [Fact]
    public async Task A_field_the_site_type_does_not_declare_answers_from_the_tenant_record()
    {
        var slug = await TenantProfileSeed.TenantAsync(_fixture, Ct, OldProfile);
        await TenantProfileSeed.StoreSiteTypeAsync(_fixture, slug, Ct, TenantProfileSeed.Declared("Logo", "url"));
        await PublishedEntryAsync(slug, new Dictionary<string, object>
        {
            ["Name"] = "Made before the move",
            ["Logo"] = "https://new.example/logo.png",
        });

        var profile = await TenantProfileSeed.PublicProfileAsync(_fixture, slug, Ct);

        TenantProfileSeed.Field(profile, "logoUrl").Should().Be("https://new.example/logo.png", "the entry is being read");
        TenantProfileSeed.Field(profile, "about").Should().Be("Old about", "this is a tenant the migration has not reached");
        TenantProfileSeed.Field(profile, "contactUrl").Should().Be("https://old.example/contact");
    }

    [Fact]
    public async Task A_declared_field_the_editor_blanked_answers_empty_and_not_the_tenant_record()
    {
        var slug = await TenantProfileSeed.TenantAsync(_fixture, Ct, OldProfile);
        await PublishedSiteAsync(slug, new Dictionary<string, object>
        {
            ["Name"] = "Site name",
            ["About"] = "New about",
            ["Email"] = "",
            ["SocialHandle"] = "   ",
        });

        var profile = await TenantProfileSeed.PublicProfileAsync(_fixture, slug, Ct);

        TenantProfileSeed.Field(profile, "about").Should().Be("New about", "the entry is being read");
        TenantProfileSeed.Field(profile, "email").Should().BeNull("blanking a field in the site entry removes it, whatever the record still holds");
        TenantProfileSeed.Field(profile, "socialHandle").Should().BeNull();
    }

    /// <summary>
    /// The tenant API refused anything but http and https for its two links. A site field can be
    /// declared as plain text by the tenant's own administrator, so the route applies the rule.
    /// </summary>
    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,alert(1)")]
    [InlineData("//evil.example/path")]
    [InlineData("evil.example")]
    public async Task A_link_in_the_site_entry_that_is_not_http_or_https_is_not_served(string value)
    {
        var slug = await TenantProfileSeed.TenantAsync(_fixture, Ct, OldProfile);
        await TenantProfileSeed.StoreSiteTypeAsync(
            _fixture, slug, Ct,
            TenantProfileSeed.Declared("Logo", "string"),
            TenantProfileSeed.Declared("LocationUrl", "string"),
            TenantProfileSeed.Declared("ContactUrl", "text"),
            TenantProfileSeed.Declared("About", "text"));
        await PublishedEntryAsync(slug, new Dictionary<string, object>
        {
            ["Name"] = "Plain text links",
            ["Logo"] = value,
            ["LocationUrl"] = value,
            ["ContactUrl"] = value,
            ["About"] = value,
        });

        var profile = await TenantProfileSeed.PublicProfileAsync(_fixture, slug, Ct);

        TenantProfileSeed.Field(profile, "about").Should().Be(value, "the entry is being read, and plain text is plain text");
        TenantProfileSeed.Field(profile, "logoUrl").Should().BeNull();
        TenantProfileSeed.Field(profile, "locationUrl").Should().BeNull();
        TenantProfileSeed.Field(profile, "contactUrl").Should().BeNull();
    }

    [Fact]
    public async Task An_https_link_in_a_site_field_declared_as_text_is_served()
    {
        var slug = await TenantProfileSeed.TenantAsync(_fixture, Ct);
        await TenantProfileSeed.StoreSiteTypeAsync(_fixture, slug, Ct, TenantProfileSeed.Declared("ContactUrl", "string"));
        await PublishedEntryAsync(slug, new Dictionary<string, object>
        {
            ["Name"] = "Plain text link",
            ["ContactUrl"] = "https://new.example/contact",
        });

        var profile = await TenantProfileSeed.PublicProfileAsync(_fixture, slug, Ct);

        TenantProfileSeed.Field(profile, "contactUrl").Should().Be("https://new.example/contact");
    }

    /// <summary>
    /// In Multi the route answers on the default slug with no tenant named, and still has to reach
    /// the named tenant's own partition for its site entry.
    /// </summary>
    [Fact]
    public async Task The_public_profile_answers_from_the_site_entry_in_Multi_with_no_tenant_named()
    {
        var slug = await MultiTenancyHost.RegisterTenantAsync(_fixture);
        await PublishedSiteAsync(slug, NewProfile());
        var multi = MultiTenancyHost.For(_fixture).CreateClient();
        multi.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, MultiTenancyHost.NextIp());

        var response = await multi.GetAsync($"/api/tenants/{slug}/public", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var profile = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        TenantProfileSeed.Field(profile, "handle").Should().Be(slug);
        TenantProfileSeed.Field(profile, "about").Should().Be("New about");
        TenantProfileSeed.Field(profile, "contactUrl").Should().Be("https://new.example/contact");
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

    /// <summary>
    /// Marking a field Sensitive is how a tenant takes it off the public route. A value left on the
    /// tenant record must not put it back.
    /// </summary>
    [Fact]
    public async Task A_site_field_marked_sensitive_answers_empty_even_when_the_tenant_record_holds_a_value()
    {
        var slug = await TenantProfileSeed.TenantAsync(_fixture, Ct, OldProfile);
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
        TenantProfileSeed.Field(profile, "email").Should().BeNull("neither the Sensitive site value nor the record's old one is served");
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
