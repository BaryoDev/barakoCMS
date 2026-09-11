using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Marten;
using Marten.Linq.MatchesSql;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// A slug identifies one entry of its content type, whatever status that entry is in.
/// </summary>
/// <remarks>
/// Nothing enforced this (#717), and the public slug route resolves with FirstOrDefaultAsync, so two
/// entries of one type sharing a slug answered whichever row Postgres handed back first. Which one
/// that is can change between requests, so the same URL served two different pages.
///
/// The rule spans every status rather than the published ones. A draft allowed to hold a taken slug
/// is a collision scheduled for the moment somebody publishes it, and the scheduler publishes on a
/// timer with nobody to report a refusal to.
/// </remarks>
[Collection("Sequential")]
public class SlugUniquenessTests
{
    private readonly IntegrationTestFixture _factory;

    public SlugUniquenessTests(IntegrationTestFixture factory) => _factory = factory;

    /// <summary>
    /// The part of the refusal that says it is about uniqueness.
    /// </summary>
    /// <remarks>
    /// Asserted on, rather than just the 400, because several other rules answer 400 for the same
    /// request: a bad slug shape is one, and a test that only checked the status code would pass
    /// against whichever rule happened to fire.
    /// </remarks>
    private const string TakenMessage = "is already used by another";

    private async Task<HttpClient> AdminAsync()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await _factory.StoredUserTokenAsync("SuperAdmin"));
        return client;
    }

    /// <summary>
    /// A type with a slug field, named per test so tests cannot collide on stored content.
    /// </summary>
    /// <remarks>
    /// <paramref name="slugFieldType"/> is "slug" except where a test needs a value the slug field
    /// type itself would reject. <c>FieldTypeRegistry</c>'s slug pattern is lower case only, so a
    /// mixed-case value on a "slug" field is refused for its shape before uniqueness is ever
    /// consulted, and a casing test written against it would pass with no uniqueness rule at all. A
    /// field merely <em>named</em> Slug is slug-addressable too (<c>PublicDelivery.SlugField</c>),
    /// which is the case where a mixed-case slug really does reach the database.
    /// </remarks>
    private async Task<string> SeedTypeAsync(string suffix, string slugFieldType = "slug")
    {
        var type = $"slugpost_{suffix}";

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = type,
            DisplayName = type,
            IsPubliclyDeliverable = true,
            Fields =
            [
                new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" },
                new FieldDefinition { Name = "Slug", DisplayName = "Slug", Type = slugFieldType },
            ],
        });
        await session.SaveChangesAsync();

        return type;
    }

    private static async Task<HttpResponseMessage> CreateAsync(
        HttpClient client, string type, string title, string slug, ContentStatus status = ContentStatus.Published)
        => await client.PostAsJsonAsync("/api/contents", new
        {
            contentType = type,
            status,
            data = new Dictionary<string, object> { ["Title"] = title, ["Slug"] = slug },
        });

    private static async Task<Guid> CreatedIdAsync(HttpResponseMessage response)
    {
        response.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            response.StatusCode, await response.Content.ReadAsStringAsync());

        using var doc = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    /// <summary>
    /// Every entry of a type holding this slug, matched with the delivery route's own predicate
    /// rather than a second copy of it, so these counts are what the route would have to choose from.
    /// </summary>
    private async Task<IReadOnlyList<Content>> EntriesHoldingSlugAsync(string type, string slug)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();

        var (sql, parameters) = barakoCMS.Features.Public.DeliveryQuery.FieldEqualsIgnoreCaseSql("Slug", slug);

        return await session.Query<Content>()
            .Where(c => c.ContentType == type && c.MatchesSql(sql, parameters))
            .ToListAsync();
    }

    [Fact]
    public async Task A_second_entry_cannot_take_a_slug_another_entry_already_holds()
    {
        var client = await AdminAsync();
        var type = await SeedTypeAsync("second");

        await CreatedIdAsync(await CreateAsync(client, type, "First", "hello-world"));

        var second = await CreateAsync(client, type, "Second", "hello-world");

        second.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "two entries of one type sharing a slug is what makes the slug route non-deterministic");
        var body = await second.Content.ReadAsStringAsync();
        body.Should().Contain(TakenMessage, "refused as a duplicate, not by some other rule");
        body.Should().Contain("hello-world",
            "the error has to name the value that is taken, or the author cannot fix it");

        var holders = await EntriesHoldingSlugAsync(type, "hello-world");
        holders.Should().HaveCount(1, "the refused create stored nothing");
        holders.Should().OnlyContain(c => c.Data["Title"].ToString() == "First");
    }

    /// <summary>
    /// The drafts decision, asserted: a draft holds its slug against a new entry.
    /// </summary>
    [Fact]
    public async Task A_slug_a_draft_holds_is_not_available_to_a_published_entry()
    {
        var client = await AdminAsync();
        var type = await SeedTypeAsync("draftholds");

        await CreatedIdAsync(await CreateAsync(client, type, "Draft", "coming-soon", ContentStatus.Draft));

        var published = await CreateAsync(client, type, "Published", "coming-soon");

        published.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "a draft that may not keep its slug is a collision waiting for whoever publishes it");
        (await published.Content.ReadAsStringAsync()).Should().Contain(TakenMessage,
            "refused as a duplicate, not by some other rule");
    }

    [Fact]
    public async Task A_slug_differing_only_in_case_is_the_same_slug()
    {
        var client = await AdminAsync();
        var type = await SeedTypeAsync("casing", slugFieldType: "string");

        await CreatedIdAsync(await CreateAsync(client, type, "First", "Hello-World"));

        var second = await CreateAsync(client, type, "Second", "hello-world");

        second.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "the slug route resolves case-insensitively, so uniqueness has to agree with it");
        (await second.Content.ReadAsStringAsync()).Should().Contain(TakenMessage,
            "and it has to be refused as a duplicate, not for the shape of the value");
    }

    [Fact]
    public async Task An_entry_may_keep_its_own_slug_on_update()
    {
        var client = await AdminAsync();
        var type = await SeedTypeAsync("keepsown");

        var id = await CreatedIdAsync(await CreateAsync(client, type, "First", "keeps-its-slug"));

        var update = await client.PutAsJsonAsync($"/api/contents/{id}", new
        {
            data = new Dictionary<string, object> { ["Title"] = "Retitled", ["Slug"] = "keeps-its-slug" },
        });

        update.IsSuccessStatusCode.Should().BeTrue(
            "an entry colliding with itself would make every edit after the first impossible: got {0}: {1}",
            update.StatusCode, await update.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_update_cannot_take_another_entrys_slug()
    {
        var client = await AdminAsync();
        var type = await SeedTypeAsync("updatetakes");

        await CreatedIdAsync(await CreateAsync(client, type, "First", "taken"));
        var second = await CreatedIdAsync(await CreateAsync(client, type, "Second", "free"));

        var update = await client.PutAsJsonAsync($"/api/contents/{second}", new
        {
            data = new Dictionary<string, object> { ["Title"] = "Second", ["Slug"] = "taken" },
        });

        update.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "an update is the other way to create a duplicate");
        (await update.Content.ReadAsStringAsync()).Should().Contain(TakenMessage,
            "refused as a duplicate, not by some other rule");

        var holders = await EntriesHoldingSlugAsync(type, "taken");
        holders.Should().HaveCount(1, "the refused update stored nothing");
        holders.Should().OnlyContain(c => c.Data["Title"].ToString() == "First");
    }

    [Fact]
    public async Task A_slug_is_unique_within_a_type_and_not_across_types()
    {
        var client = await AdminAsync();
        var one = await SeedTypeAsync("typeone");
        var other = await SeedTypeAsync("typetwo");

        await CreatedIdAsync(await CreateAsync(client, one, "First", "shared-slug"));

        var elsewhere = await CreateAsync(client, other, "Also first", "shared-slug");

        elsewhere.IsSuccessStatusCode.Should().BeTrue(
            "the slug route is per type, so two types may each have a 'shared-slug' entry: got {0}: {1}",
            elsewhere.StatusCode, await elsewhere.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_type_with_no_slug_field_is_unaffected()
    {
        var client = await AdminAsync();
        var type = $"noslug_{Guid.NewGuid():n}";

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(),
                Name = type,
                DisplayName = type,
                Fields = [new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" }],
            });
            await session.SaveChangesAsync();
        }

        var first = await client.PostAsJsonAsync("/api/contents", new
        {
            contentType = type,
            data = new Dictionary<string, object> { ["Title"] = "Same title" },
        });
        var second = await client.PostAsJsonAsync("/api/contents", new
        {
            contentType = type,
            data = new Dictionary<string, object> { ["Title"] = "Same title" },
        });

        first.IsSuccessStatusCode.Should().BeTrue("got {0}", first.StatusCode);
        second.IsSuccessStatusCode.Should().BeTrue(
            "a type with no slug field has nothing to be unique about: got {0}: {1}",
            second.StatusCode, await second.Content.ReadAsStringAsync());
    }
}
