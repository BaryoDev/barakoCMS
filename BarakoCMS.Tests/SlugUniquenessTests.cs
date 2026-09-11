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

    /// <summary>
    /// The archived half of the same decision, and the one the docs promise explicitly.
    /// </summary>
    /// <remarks>
    /// Excluding Archived is the one-line change somebody will reach for the first time they want to
    /// retire a page and publish a replacement on its URL. It is a real request, and the answer is a
    /// redirect plus a slug change on the retired entry, not a hole in the rule: an archived entry
    /// can be un-archived with no request to refuse.
    /// </remarks>
    [Fact]
    public async Task An_archived_entry_still_holds_its_slug()
    {
        var client = await AdminAsync();
        var type = await SeedTypeAsync("archivedholds");

        await CreatedIdAsync(await CreateAsync(client, type, "Retired", "retired-page", ContentStatus.Archived));

        var replacement = await CreateAsync(client, type, "Replacement", "retired-page");

        replacement.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "an archived entry that can be published again is not a free slug");
        (await replacement.Content.ReadAsStringAsync()).Should().Contain(TakenMessage,
            "refused as a duplicate, not by some other rule");
    }

    /// <summary>
    /// A rollback that leaves the slug as it is goes through.
    /// </summary>
    /// <remarks>
    /// The rollback route passes the entry's own id to the validator so the entry is excluded from
    /// its own uniqueness check. Lose that argument and every rollback of an entry with a slug
    /// collides with the slug the entry already has, which is a 400 on a route whose whole job is
    /// putting back data that was valid when it was written.
    /// </remarks>
    [Fact]
    public async Task A_rollback_that_leaves_the_slug_alone_is_restored()
    {
        var client = await AdminAsync();
        var type = await SeedTypeAsync("rollbackkeeps");

        var id = await CreatedIdAsync(await CreateAsync(client, type, "First title", "rolled-back"));

        var update = await client.PutAsJsonAsync($"/api/contents/{id}", new
        {
            data = new Dictionary<string, object> { ["Title"] = "Second title", ["Slug"] = "rolled-back" },
        });
        update.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            update.StatusCode, await update.Content.ReadAsStringAsync());

        var version = await VersionHoldingAsync(client, id, "Title", "First title");

        var rollback = await client.PostAsJsonAsync($"/api/contents/{id}/rollback/{version}", new { });

        rollback.IsSuccessStatusCode.Should().BeTrue(
            "the entry is not another entry, so its own slug cannot be in its way: got {0}: {1}",
            rollback.StatusCode, await rollback.Content.ReadAsStringAsync());

        var stored = await client.GetAsync($"/api/contents/{id}");
        using var doc = System.Text.Json.JsonDocument.Parse(await stored.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("data").GetProperty("Title").GetString()
            .Should().Be("First title", "a rollback that answers 200 has to have restored something");
    }

    /// <summary>
    /// And a rollback that would restore a slug somebody else has taken since is refused.
    /// </summary>
    /// <remarks>
    /// The pair matters: the test above passes with no rule on this route at all, and this one
    /// passes if the route refuses every rollback. Together they pin the rule as applying here with
    /// the entry excluded from it.
    /// </remarks>
    [Fact]
    public async Task A_rollback_cannot_restore_a_slug_another_entry_has_taken_since()
    {
        var client = await AdminAsync();
        var type = await SeedTypeAsync("rollbacktaken");

        var id = await CreatedIdAsync(await CreateAsync(client, type, "Moved on", "old-slug"));

        var moved = await client.PutAsJsonAsync($"/api/contents/{id}", new
        {
            data = new Dictionary<string, object> { ["Title"] = "Moved on", ["Slug"] = "new-slug" },
        });
        moved.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            moved.StatusCode, await moved.Content.ReadAsStringAsync());

        await CreatedIdAsync(await CreateAsync(client, type, "Took the slug", "old-slug"));

        var version = await VersionHoldingAsync(client, id, "Slug", "old-slug");

        var rollback = await client.PostAsJsonAsync($"/api/contents/{id}/rollback/{version}", new { });

        rollback.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "restoring an old version is a write, and it cannot write a slug that is now taken");
        (await rollback.Content.ReadAsStringAsync()).Should().Contain(TakenMessage,
            "refused as a duplicate, not by some other rule");

        var holders = await EntriesHoldingSlugAsync(type, "old-slug");
        holders.Should().HaveCount(1, "the refused rollback stored nothing");
        holders.Should().OnlyContain(c => c.Data["Title"].ToString() == "Took the slug");
    }

    /// <summary>
    /// Bulk import goes through the same validator, so a row carrying a slug an existing entry holds
    /// is refused and, by default, takes the whole batch with it.
    /// </summary>
    /// <remarks>
    /// Two rows of one batch sharing a slug with each other are still both accepted: the module
    /// validates each row against what is stored, and nothing in the batch is stored yet. That gap
    /// is stated in the changelog and left to a follow-up rather than pinned here, because a test
    /// asserting it would have to be deleted to fix it.
    /// </remarks>
    [Fact]
    public async Task A_bulk_import_row_cannot_take_a_slug_an_entry_already_holds()
    {
        var client = await AdminAsync();
        var type = await SeedTypeAsync("bulkimport");

        await CreatedIdAsync(await CreateAsync(client, type, "First", "imported-slug"));

        var import = await client.PostAsJsonAsync("/api/import/content", new
        {
            contentType = type,
            records = new[]
            {
                new Dictionary<string, object> { ["Title"] = "Row zero", ["Slug"] = "fresh-slug" },
                new Dictionary<string, object> { ["Title"] = "Row one", ["Slug"] = "imported-slug" },
            },
        });

        import.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "a spreadsheet is the likeliest source of a duplicate slug, so the import path needs the rule too");

        var body = await import.Content.ReadAsStringAsync();
        body.Should().Contain(TakenMessage, "refused as a duplicate, not by some other rule");

        using var report = System.Text.Json.JsonDocument.Parse(body);
        report.RootElement.GetProperty("errors").EnumerateArray()
            .Select(e => e.GetProperty("row").GetInt32())
            .Should().BeEquivalentTo([1], "the report names the row, so the file can be fixed");

        var holders = await EntriesHoldingSlugAsync(type, "imported-slug");
        holders.Should().HaveCount(1, "the refused import stored nothing");
        holders.Should().OnlyContain(c => c.Data["Title"].ToString() == "First");

        var fresh = await EntriesHoldingSlugAsync(type, "fresh-slug");
        fresh.Should().BeEmpty("one bad row in an all-or-nothing import writes none of them");
    }

    /// <summary>
    /// The other half of #717: a deployment that already holds duplicates serves the same entry
    /// every time.
    /// </summary>
    /// <remarks>
    /// Uniqueness on the way in does nothing for rows already stored, and the release deliberately
    /// does not rewrite anybody's slugs, so the route has to pick one and keep picking it. Seeded
    /// straight into the database because the authoring API now refuses exactly this state.
    ///
    /// The newer entry is stored first on purpose. An unordered FirstOrDefaultAsync over a small
    /// table hands back what the sequential scan reaches first, which is the row inserted first, so
    /// a test seeded the other way round would pass against the unordered query and prove nothing.
    /// </remarks>
    [Fact]
    public async Task A_slug_two_entries_already_share_resolves_to_the_older_entry()
    {
        var type = await SeedTypeAsync("alreadyduplicated");
        const string slug = "duplicated";

        var older = DateTime.UtcNow.AddDays(-7);

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new Content
            {
                Id = Guid.NewGuid(),
                ContentType = type,
                Status = ContentStatus.Published,
                Sensitivity = SensitivityLevel.Public,
                Data = new Dictionary<string, object> { ["Title"] = "Newer", ["Slug"] = slug },
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            session.Store(new Content
            {
                Id = Guid.NewGuid(),
                ContentType = type,
                Status = ContentStatus.Published,
                Sensitivity = SensitivityLevel.Public,
                Data = new Dictionary<string, object> { ["Title"] = "Older", ["Slug"] = slug },
                CreatedAt = older,
                UpdatedAt = older,
            });
            await session.SaveChangesAsync();
        }

        var holders = await EntriesHoldingSlugAsync(type, slug);
        holders.Should().HaveCount(2, "the seeded collision is the whole premise of this test");

        var anonymous = _factory.CreateClient();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var response = await anonymous.GetAsync($"/api/public/{type}/{slug}");
            response.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
                response.StatusCode, await response.Content.ReadAsStringAsync());

            using var doc = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            doc.RootElement.GetProperty("data").GetProperty("Title").GetString()
                .Should().Be("Older",
                    "the entry that held the slug first is the one whose links are already published, "
                    + "and whichever one is chosen it has to be the same one on attempt {0}", attempt);
        }
    }

    /// <summary>The id of the version in this entry's history whose field holds this value.</summary>
    private static async Task<Guid> VersionHoldingAsync(HttpClient client, Guid id, string field, string value)
    {
        var history = await client.GetAsync($"/api/contents/{id}/history");
        history.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            history.StatusCode, await history.Content.ReadAsStringAsync());

        using var doc = System.Text.Json.JsonDocument.Parse(await history.Content.ReadAsStringAsync());

        var matches = doc.RootElement.GetProperty("items").EnumerateArray()
            .Where(v => v.TryGetProperty("data", out var data)
                        && data.TryGetProperty(field, out var held)
                        && held.GetString() == value)
            .Select(v => v.GetProperty("versionId").GetGuid())
            .ToList();

        matches.Should().NotBeEmpty(
            $"the version holding {field} '{value}' has to be in the history to roll back to");
        return matches[0];
    }

    /// <summary>
    /// A request that is already being refused for another reason is not also told about the slug.
    /// </summary>
    /// <remarks>
    /// The uniqueness check is a query the slug lookup's own comment explains cannot use an index, so
    /// it runs only when every other rule passed, and a bulk import of N rows pays it for the rows
    /// that are otherwise valid rather than all N. The missing error is the visible half of that, and
    /// the only half a test can see from out here.
    /// </remarks>
    [Fact]
    public async Task A_request_failing_another_rule_is_not_also_checked_for_uniqueness()
    {
        var client = await AdminAsync();
        var type = "slugpost_otherrule";

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(),
                Name = type,
                DisplayName = type,
                IsPubliclyDeliverable = true,
                Fields =
                [
                    new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string", IsRequired = true },
                    new FieldDefinition { Name = "Slug", DisplayName = "Slug", Type = "slug" },
                ],
            });
            await session.SaveChangesAsync();
        }

        await CreatedIdAsync(await CreateAsync(client, type, "First", "both-wrong"));

        var second = await client.PostAsJsonAsync("/api/contents", new
        {
            contentType = type,
            status = ContentStatus.Published,
            data = new Dictionary<string, object> { ["Slug"] = "both-wrong" },
        });

        second.StatusCode.Should().Be(HttpStatusCode.BadRequest, "the required Title is missing");
        var body = await second.Content.ReadAsStringAsync();
        body.Should().Contain("is required", "the rule that did run has to be reported");
        body.Should().NotContain(TakenMessage,
            "the uniqueness query is skipped on a request that is already refused");
    }
}
