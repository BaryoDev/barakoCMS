using System.Net;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Core.Interfaces;
using barakoCMS.Models;
using Xunit;

namespace BarakoCMS.Tests.Features.Public;

/// <summary>
/// A delivered reference names only entries delivery would serve. A reference to a draft is left
/// out of the entry on every anonymous route, not only when it is resolved with include.
/// </summary>
/// <remarks>
/// Each entry points at one published target and one draft target, through a list and through a
/// single reference, so the published id coming back shows the field is still delivered and the
/// draft id missing shows the check and not a missing field.
/// </remarks>
[Collection("Sequential")]
public class PublicReferenceDeliveryTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;

    public PublicReferenceDeliveryTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <param name="Closed">Every target delivery would not serve, the draft included.</param>
    private sealed record Seed(
        string Type, string Target, Guid Entry, string Slug, string Needle, Guid Published, Guid Draft, Guid TargetsDraft,
        IReadOnlyList<Guid> Closed);

    private async Task<Seed> SeedAsync()
    {
        var tag = Guid.NewGuid().ToString("n")[..10];
        var type = "refdel" + tag;
        var target = "reftgt" + tag;
        var slug = "ref-" + tag;
        var needle = "needle" + tag;
        var published = Guid.NewGuid();
        var draft = Guid.NewGuid();
        var targetsDraft = Guid.NewGuid();
        var sensitive = Guid.NewGuid();
        var ofClosedType = Guid.NewGuid();
        var inOtherTenant = Guid.NewGuid();
        var entry = Guid.NewGuid();
        var closedType = "refcls" + tag;
        var otherTenant = "refoth-" + tag;

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(), Name = target, DisplayName = target, IsPubliclyDeliverable = true,
            Fields =
            [
                new FieldDefinition { Name = "Name", DisplayName = "Name", Type = "string" },
                new FieldDefinition { Name = "Next", DisplayName = "Next", Type = "reference", ReferenceType = target },
            ],
        });
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(), Name = type, DisplayName = type, IsPubliclyDeliverable = true,
            Fields =
            [
                new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" },
                new FieldDefinition { Name = "Slug", DisplayName = "Slug", Type = "slug" },
                new FieldDefinition { Name = "Related", DisplayName = "Related", Type = "reference", ReferenceType = target, Multiple = true },
                new FieldDefinition { Name = "Description", DisplayName = "Description", Type = "reference", ReferenceType = target },
                new FieldDefinition { Name = "Pinned", DisplayName = "Pinned", Type = "reference", ReferenceType = target },
            ],
        });
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(), Name = closedType, DisplayName = closedType, IsPubliclyDeliverable = false,
            Fields = [new FieldDefinition { Name = "Name", DisplayName = "Name", Type = "string" }],
        });
        session.Store(new Content
        {
            Id = sensitive, ContentType = target, Status = ContentStatus.Published, Sensitivity = SensitivityLevel.Sensitive,
            Data = new() { ["Name"] = "Kept back" },
        });
        session.Store(new Content
        {
            Id = ofClosedType, ContentType = closedType, Status = ContentStatus.Published, Sensitivity = SensitivityLevel.Public,
            Data = new() { ["Name"] = "Type not delivered" },
        });

        session.Store(new Content
        {
            Id = published, ContentType = target, Status = ContentStatus.Published, Sensitivity = SensitivityLevel.Public,
            Data = new() { ["Name"] = "Out there", ["Next"] = targetsDraft.ToString() },
        });
        session.Store(new Content
        {
            Id = draft, ContentType = target, Status = ContentStatus.Draft, Sensitivity = SensitivityLevel.Public,
            Data = new() { ["Name"] = "Not yet" },
        });
        session.Store(new Content
        {
            Id = targetsDraft, ContentType = target, Status = ContentStatus.Draft, Sensitivity = SensitivityLevel.Public,
            Data = new() { ["Name"] = "Also not yet" },
        });
        session.Store(new Content
        {
            Id = entry, ContentType = type, Status = ContentStatus.Published, Sensitivity = SensitivityLevel.Public,
            Data = new()
            {
                ["Title"] = $"{needle} points both ways",
                ["Slug"] = slug,
                ["Related"] = new List<string>
                {
                    published.ToString(), draft.ToString(), sensitive.ToString(), ofClosedType.ToString(), inOtherTenant.ToString(),
                },
                ["Description"] = draft.ToString(),
                ["Pinned"] = published.ToString(),
            },
            SearchText = $"{needle} points both ways",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        await session.SaveChangesAsync(Ct);

        // Published, Public and of the same type, in another tenant: this tenant's delivery cannot read it.
        var store = _factory.Services.GetRequiredService<IDocumentStore>();
        await using (var other = store.LightweightSession(otherTenant))
        {
            other.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(), Name = target, DisplayName = target, IsPubliclyDeliverable = true,
                Fields = [new FieldDefinition { Name = "Name", DisplayName = "Name", Type = "string" }],
            });
            other.Store(new Content
            {
                Id = inOtherTenant, ContentType = target, Status = ContentStatus.Published, Sensitivity = SensitivityLevel.Public,
                Data = new() { ["Name"] = "Somewhere else" },
            });
            await other.SaveChangesAsync(Ct);
        }

        return new Seed(type, target, entry, slug, needle, published, draft, targetsDraft,
            [draft, sensitive, ofClosedType, inOtherTenant]);
    }

    private async Task<string> OkBodyAsync(string path)
    {
        var response = await _client.GetAsync(path, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return body;
    }

    private static void OnlyThePublishedTargetIsNamed(JsonElement entry, Seed seed)
    {
        var data = entry.GetProperty("data");

        var related = data.GetProperty("Related").EnumerateArray().Select(e => e.GetString()).ToList();
        related.Should().HaveCount(1,
            "the list keeps the published target and drops a draft, a Sensitive entry, an entry of a type "
            + "that is not delivered and an entry in another tenant");
        related.Should().Equal(seed.Published.ToString());

        data.TryGetProperty("Description", out _).Should().BeFalse(
            "a single reference to a draft is left out, as include leaves it out");
        data.GetProperty("Pinned").GetString().Should().Be(seed.Published.ToString(),
            "a single reference to a published target is still its id");

        seed.Closed.Should().HaveCount(4);
        foreach (var closed in seed.Closed)
            entry.GetRawText().Should().NotContain(closed.ToString());
    }

    [Fact]
    public async Task The_list_names_only_the_published_target()
    {
        var seed = await SeedAsync();

        var root = JsonDocument.Parse(await OkBodyAsync($"/api/public/{seed.Type}")).RootElement;
        var items = root.GetProperty("items").EnumerateArray().ToList();

        items.Should().HaveCount(1);
        OnlyThePublishedTargetIsNamed(items[0], seed);
    }

    [Fact]
    public async Task The_read_by_slug_names_only_the_published_target()
    {
        var seed = await SeedAsync();

        var entry = JsonDocument.Parse(await OkBodyAsync($"/api/public/{seed.Type}/{seed.Slug}")).RootElement;

        entry.GetProperty("id").GetGuid().Should().Be(seed.Entry);
        OnlyThePublishedTargetIsNamed(entry, seed);
    }

    [Fact]
    public async Task Search_names_only_the_published_target()
    {
        var seed = await SeedAsync();

        var root = JsonDocument.Parse(await OkBodyAsync($"/api/public/{seed.Type}/search?q={seed.Needle}")).RootElement;
        var results = root.GetProperty("results").EnumerateArray().ToList();

        results.Should().HaveCount(1);
        OnlyThePublishedTargetIsNamed(results[0], seed);
    }

    /// <summary>
    /// A filter on a reference field answers against the ids delivery shows, so naming a draft finds
    /// nothing, the same as naming an id no entry holds.
    /// </summary>
    [Fact]
    public async Task A_reference_filter_naming_an_entry_delivery_would_not_serve_matches_nothing()
    {
        var seed = await SeedAsync();

        async Task<int> CountAsync(string query)
        {
            var root = JsonDocument.Parse(await OkBodyAsync($"/api/public/{seed.Type}?{query}")).RootElement;
            return root.GetProperty("items").GetArrayLength();
        }

        (await CountAsync($"filter[Related][has]={seed.Published}")).Should().Be(1, "the control: a delivered id still matches");
        (await CountAsync($"filter[Pinned][eq]={seed.Published}")).Should().Be(1);

        (await CountAsync($"filter[Description][eq]={seed.Draft}")).Should().Be(0,
            "the entry is delivered without Description, so it does not hold the draft's id");
        (await CountAsync($"filter[Related][has]={seed.Draft}")).Should().Be(0);
        (await CountAsync($"filter[Related][eq]={seed.Closed[1]}")).Should().Be(0, "a Sensitive target");
        (await CountAsync($"filter[Related][has]={seed.Closed[2]}")).Should().Be(0, "a target of a type that is not delivered");
        (await CountAsync($"filter[Related][has]={seed.Closed[3]}")).Should().Be(0, "a target in another tenant");
        (await CountAsync($"filter[Related][ne]={seed.Draft}")).Should().Be(1,
            "the delivered list does not hold the draft, so the entry is one that does not");

        var search = JsonDocument.Parse(await OkBodyAsync(
            $"/api/public/{seed.Type}/search?q={seed.Needle}&filter[Related][has]={seed.Draft}")).RootElement;
        search.GetProperty("results").GetArrayLength().Should().Be(0, "search applies the same rule");
    }

    [Fact]
    public async Task The_feed_does_not_carry_the_draft_id()
    {
        var seed = await SeedAsync();

        var feed = await OkBodyAsync($"/api/public/{seed.Type}/feed.xml");

        feed.Should().Contain(seed.Entry.ToString(), "the entry is in the feed");
        feed.Should().NotContain(seed.Draft.ToString(),
            "Description is one of the names the feed reads a summary from, and here it holds a draft's id");
    }

    [Fact]
    public async Task An_included_target_names_only_published_entries_itself()
    {
        var seed = await SeedAsync();

        var root = JsonDocument.Parse(await OkBodyAsync($"/api/public/{seed.Type}?include=Related")).RootElement;
        var items = root.GetProperty("items").EnumerateArray().ToList();
        items.Should().HaveCount(1);

        var resolved = items[0].GetProperty("data").GetProperty("Related").EnumerateArray().ToList();
        resolved.Should().HaveCount(1);
        resolved[0].GetProperty("id").GetGuid().Should().Be(seed.Published);
        resolved[0].GetProperty("data").GetProperty("Name").GetString().Should().Be("Out there");
        resolved[0].GetProperty("data").TryGetProperty("Next", out _).Should().BeFalse(
            "the resolved target points at a draft, and it is read as it would be on its own");
        root.GetRawText().Should().NotContain(seed.TargetsDraft.ToString());
    }

    [Fact]
    public async Task A_reference_to_a_published_target_is_still_delivered_as_its_id()
    {
        var seed = await SeedAsync();

        var root = JsonDocument.Parse(await OkBodyAsync($"/api/public/{seed.Target}")).RootElement;
        var items = root.GetProperty("items").EnumerateArray().ToList();

        items.Should().HaveCount(1, "only the one target is published");
        items[0].GetProperty("id").GetGuid().Should().Be(seed.Published);
        items[0].GetProperty("data").TryGetProperty("Next", out _).Should().BeFalse("Next names a draft");
        items[0].GetProperty("data").GetProperty("Name").GetString().Should().Be("Out there");
    }

    [Fact]
    public async Task The_module_projector_names_only_the_published_target()
    {
        var seed = await SeedAsync();

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var projector = scope.ServiceProvider.GetRequiredService<IPublicContentProjector>();
        var entry = await session.LoadAsync<Content>(seed.Entry, Ct);
        var definition = await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name == seed.Type, Ct);

        var projected = await projector.ProjectAsync(entry!, definition, Ct);

        projected.Should().NotBeNull();
        projected!.Data.Should().ContainKey("Related");
        projected.Data.Should().NotContainKey("Description");
        JsonSerializer.Serialize(projected.Data["Related"]).Should().Contain(seed.Published.ToString())
            .And.NotContain(seed.Draft.ToString());
    }
}
