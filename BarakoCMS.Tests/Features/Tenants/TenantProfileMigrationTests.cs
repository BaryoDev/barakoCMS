using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using barakoCMS.Models;

// The obsolete Tenant members are the old home this file moves values out of.
#pragma warning disable CS0618

namespace BarakoCMS.Tests.Features.Tenants;

/// <summary>
/// <c>migrations/4.6.0/tenant-profile-to-site.sql</c> moves a tenant's stored profile into its
/// published site entry, says which tenants and values it left behind, and its rollback puts the
/// profile back (#885).
/// </summary>
/// <remarks>
/// The files are run as they are, against the database the suite shares, so what they write is read
/// back through the API and through Marten and not through a copy of their own assumptions. That
/// means a run also visits tenants other tests left behind. Each test therefore looks only at the
/// tenants it made, and the files only ever act on a tenant that holds profile values, which no
/// other class stores.
///
/// The site type is stored by hand with two fields, which is the type a tenant made from the
/// blueprint before the profile fields were in it.
/// </remarks>
[Collection("Sequential")]
public class TenantProfileMigrationTests
{
    private const string Up = "tenant-profile-to-site.sql";
    private const string Down = "rollback-tenant-profile-to-site.sql";

    private readonly IntegrationTestFixture _fixture;

    public TenantProfileMigrationTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void Profile(Tenant t)
    {
        t.LogoUrl = "https://club.example/logo.png";
        t.About = "A club in Koronadal";
        t.Location = "Koronadal";
        t.LocationUrl = "https://maps.example/club";
        t.SocialHandle = "@club";
        t.Email = "hello@club.example";
        t.ContactUrl = "https://club.example/contact";
    }

    private async Task StoreSiteTypeAsync(string slug, params FieldDefinition[] extraFields)
    {
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession(slug);
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = "site",
            DisplayName = "Site",
            IsPubliclyDeliverable = true,
            IsSingleton = true,
            Fields =
            [
                new FieldDefinition { Name = "Name", DisplayName = "Site name", Type = "string", IsRequired = true },
                new FieldDefinition { Name = "Logo", DisplayName = "Logo", Type = "url" },
                .. extraFields,
            ],
        });
        await session.SaveChangesAsync(Ct);
    }

    private async Task<Guid> PublishedEntryAsync(string slug, Dictionary<string, object> data)
    {
        var admin = await TenantProfileSeed.AdminInAsync(_fixture, slug, Ct);
        var id = await TenantProfileSeed.CreateSiteEntryAsync(admin, data, Ct);
        await TenantProfileSeed.PublishAsync(admin, id, Ct);
        return id;
    }

    /// <summary>What anonymous delivery serves as the tenant's site: the data of its one entry.</summary>
    private async Task<JsonElement> DeliveredSiteAsync(string slug)
    {
        var response = await TenantProfileSeed.Anonymous(_fixture, slug).GetAsync("/api/public/site", Ct);
        response.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var items = body.GetProperty("items");
        items.GetArrayLength().Should().Be(1, "the tenant has one published site entry");
        return items[0].GetProperty("data");
    }

    private static string? Delivered(JsonElement data, string field) =>
        data.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Runs one of the two files as written and returns the notices it raised.</summary>
    private async Task<List<string>> RunAsync(string file)
    {
        var sql = await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(), "migrations", "4.6.0", file), Ct);
        var notices = new List<string>();

        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        connection.Notice += (_, e) => notices.Add(e.Notice.MessageText);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
        return notices;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "migrations")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the test binary should sit under the repository");
        return directory!.FullName;
    }

    [Fact]
    public async Task The_migration_moves_a_tenants_profile_into_its_published_site_entry()
    {
        var slug = await TenantProfileSeed.TenantAsync(_fixture, Ct, Profile);
        await StoreSiteTypeAsync(slug);
        var entryId = await PublishedEntryAsync(slug, new Dictionary<string, object> { ["Name"] = "The club" });
        (await DeliveredSiteAsync(slug)).TryGetProperty("About", out _).Should().BeFalse("nothing has moved yet");

        await RunAsync(Up);

        var site = await DeliveredSiteAsync(slug);
        Delivered(site, "Name").Should().Be("The club", "what the entry already held is untouched");
        Delivered(site, "Logo").Should().Be("https://club.example/logo.png");
        Delivered(site, "About").Should().Be("A club in Koronadal");
        Delivered(site, "Location").Should().Be("Koronadal");
        Delivered(site, "LocationUrl").Should().Be("https://maps.example/club");
        Delivered(site, "SocialHandle").Should().Be("@club");
        Delivered(site, "Email").Should().Be("hello@club.example");
        Delivered(site, "ContactUrl").Should().Be("https://club.example/contact");

        var tenant = await TenantProfileSeed.StoredAsync(_fixture, slug, Ct);
        tenant.Name.Should().Be($"Name of {slug}");
        tenant.IsActive.Should().BeTrue();
        new[] { tenant.LogoUrl, tenant.About, tenant.Location, tenant.LocationUrl, tenant.SocialHandle, tenant.Email, tenant.ContactUrl }
            .Should().HaveCount(7).And.OnlyContain(v => v == null, "a moved value is blanked on the tenant record");

        var profile = await TenantProfileSeed.PublicProfileAsync(_fixture, slug, Ct);
        TenantProfileSeed.Field(profile, "about").Should().Be("A club in Koronadal", "the profile route answers the same after the move");
        TenantProfileSeed.Field(profile, "logoUrl").Should().Be("https://club.example/logo.png");
        TenantProfileSeed.Field(profile, "contactUrl").Should().Be("https://club.example/contact");

        // The type the file wrote to has to be one the API still reads and validates against, or
        // the next save of the entry fails.
        var admin = await TenantProfileSeed.AdminInAsync(_fixture, slug, Ct);
        var edited = await admin.PutAsJsonAsync(
            $"/api/contents/{entryId}",
            new { id = entryId, data = new Dictionary<string, object> { ["Name"] = "The club", ["About"] = "Edited in the console" } },
            Ct);
        edited.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", edited.StatusCode, await edited.Content.ReadAsStringAsync(Ct));
        Delivered(await DeliveredSiteAsync(slug), "About").Should().Be("Edited in the console");
    }

    [Fact]
    public async Task A_second_run_moves_nothing_more()
    {
        var slug = await TenantProfileSeed.TenantAsync(_fixture, Ct, t => t.About = "Moved once");
        await StoreSiteTypeAsync(slug);
        await PublishedEntryAsync(slug, new Dictionary<string, object> { ["Name"] = "Twice" });

        await RunAsync(Up);
        var first = (await DeliveredSiteAsync(slug)).GetRawText();
        var fieldsAfterFirst = await SiteFieldNamesAsync(slug);

        await RunAsync(Up);

        (await DeliveredSiteAsync(slug)).GetRawText().Should().Be(first);
        fieldsAfterFirst.Should().Equal("Name", "Logo", "About");
        (await SiteFieldNamesAsync(slug)).Should().Equal(fieldsAfterFirst, "a second run adds no field twice");
        (await TenantProfileSeed.StoredAsync(_fixture, slug, Ct)).About.Should().BeNull();
    }

    private async Task<List<string>> SiteFieldNamesAsync(string slug)
    {
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.QuerySession(slug);
        var site = await session.Query<ContentTypeDefinition>().SingleAsync(d => d.Name == "site", Ct);
        return site.Fields.Select(f => f.Name).ToList();
    }

    [Fact]
    public async Task A_tenant_with_no_published_site_entry_is_left_alone_and_named()
    {
        var noSite = await TenantProfileSeed.TenantAsync(_fixture, Ct, Profile);
        var draftOnly = await TenantProfileSeed.TenantAsync(_fixture, Ct, Profile);
        await StoreSiteTypeAsync(draftOnly);
        var admin = await TenantProfileSeed.AdminInAsync(_fixture, draftOnly, Ct);
        await TenantProfileSeed.CreateSiteEntryAsync(admin, new Dictionary<string, object> { ["Name"] = "Draft" }, Ct);

        var notices = await RunAsync(Up);

        foreach (var slug in new[] { noSite, draftOnly })
        {
            var tenant = await TenantProfileSeed.StoredAsync(_fixture, slug, Ct);
            tenant.About.Should().Be("A club in Koronadal", "{0} has nowhere published to move to", slug);
            tenant.ContactUrl.Should().Be("https://club.example/contact");
            notices.Should().Contain(n => n.Contains(slug) && n.Contains("left on the tenant document"),
                "a tenant that is skipped is reported, not passed over");
            var profile = await TenantProfileSeed.PublicProfileAsync(_fixture, slug, Ct);
            TenantProfileSeed.Field(profile, "about").Should().Be("A club in Koronadal", "the route falls back to the record");
        }
    }

    [Fact]
    public async Task Where_the_site_entry_already_has_a_different_value_the_entry_wins_and_both_are_kept()
    {
        var slug = await TenantProfileSeed.TenantAsync(_fixture, Ct, Profile);
        await StoreSiteTypeAsync(slug);
        await PublishedEntryAsync(slug, new Dictionary<string, object>
        {
            ["Name"] = "Two logos",
            ["Logo"] = "https://club.example/new-logo.png",
        });

        var notices = await RunAsync(Up);

        Delivered(await DeliveredSiteAsync(slug), "Logo").Should().Be("https://club.example/new-logo.png");
        var tenant = await TenantProfileSeed.StoredAsync(_fixture, slug, Ct);
        tenant.LogoUrl.Should().Be("https://club.example/logo.png", "the losing value is kept, not dropped");
        tenant.About.Should().BeNull("the fields that did not clash moved");
        notices.Should().Contain(n => n.Contains(slug) && n.Contains("LogoUrl") && n.Contains("different"));
        var profile = await TenantProfileSeed.PublicProfileAsync(_fixture, slug, Ct);
        TenantProfileSeed.Field(profile, "logoUrl").Should().Be("https://club.example/new-logo.png");
    }

    [Fact]
    public async Task A_value_that_would_be_hidden_or_refused_in_the_site_entry_stays_on_the_tenant_record()
    {
        var slug = await TenantProfileSeed.TenantAsync(_fixture, Ct, t =>
        {
            t.Email = "hello@club.example";
            t.ContactUrl = "club.example/contact";
            t.About = "Still moves";
        });
        await StoreSiteTypeAsync(slug, new FieldDefinition
        {
            Name = "Email", DisplayName = "Email", Type = "string", Sensitivity = SensitivityLevel.Sensitive,
        });
        await PublishedEntryAsync(slug, new Dictionary<string, object> { ["Name"] = "Careful" });

        var notices = await RunAsync(Up);

        var tenant = await TenantProfileSeed.StoredAsync(_fixture, slug, Ct);
        tenant.Email.Should().Be("hello@club.example", "moving it into a Sensitive field would take it off the public route");
        tenant.ContactUrl.Should().Be("club.example/contact", "a url field would refuse it on the next save");
        tenant.About.Should().BeNull();
        notices.Should().Contain(n => n.Contains(slug) && n.Contains("Email") && n.Contains("not Public"));
        notices.Should().Contain(n => n.Contains(slug) && n.Contains("ContactUrl") && n.Contains("not an http"));
        var site = await DeliveredSiteAsync(slug);
        Delivered(site, "About").Should().Be("Still moves");
        site.TryGetProperty("ContactUrl", out _).Should().BeFalse();
    }

    [Fact]
    public async Task An_event_sourced_site_type_is_left_alone_and_named()
    {
        var slug = await TenantProfileSeed.TenantAsync(_fixture, Ct, t => t.About = "Stream is the record");
        await StoreSiteTypeAsync(slug);
        var entryId = Guid.NewGuid();
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using (var session = store.LightweightSession(slug))
        {
            session.Store(new ContentTypeSourcingPolicy { Name = "site", EventSourced = true });
            session.Store(new Content
            {
                Id = entryId,
                ContentType = "site",
                Status = ContentStatus.Published,
                Data = new Dictionary<string, object> { ["Name"] = "Sourced" },
            });
            await session.SaveChangesAsync(Ct);
        }

        var notices = await RunAsync(Up);

        (await TenantProfileSeed.StoredAsync(_fixture, slug, Ct)).About.Should().Be("Stream is the record");
        notices.Should().Contain(n => n.Contains(slug) && n.Contains("event sourced"));
        await using var query = store.QuerySession(slug);
        var entry = await query.LoadAsync<Content>(entryId, Ct);
        entry.Should().NotBeNull();
        entry!.Data.Keys.Should().Equal("Name");
    }

    [Fact]
    public async Task The_rollback_puts_the_profile_back_on_the_tenant_record()
    {
        var slug = await TenantProfileSeed.TenantAsync(_fixture, Ct, Profile);
        await StoreSiteTypeAsync(slug);
        await PublishedEntryAsync(slug, new Dictionary<string, object> { ["Name"] = "There and back" });
        await RunAsync(Up);
        (await TenantProfileSeed.StoredAsync(_fixture, slug, Ct)).About.Should().BeNull("the move happened");

        await RunAsync(Down);
        await RunAsync(Down);

        var tenant = await TenantProfileSeed.StoredAsync(_fixture, slug, Ct);
        tenant.LogoUrl.Should().Be("https://club.example/logo.png");
        tenant.About.Should().Be("A club in Koronadal");
        tenant.Location.Should().Be("Koronadal");
        tenant.LocationUrl.Should().Be("https://maps.example/club");
        tenant.SocialHandle.Should().Be("@club");
        tenant.Email.Should().Be("hello@club.example");
        tenant.ContactUrl.Should().Be("https://club.example/contact");
        Delivered(await DeliveredSiteAsync(slug), "About").Should().Be("A club in Koronadal", "the rollback takes nothing off the entry");
    }
}
