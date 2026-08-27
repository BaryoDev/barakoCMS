using System.Net;
using System.Net.Http.Json;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Filtering and sorting the public delivery list, against a real database.
/// </summary>
/// <remarks>
/// The unit tests in <see cref="DeliveryQueryTests"/> prove which filters are refused. These prove
/// the accepted ones cannot widen what is visible: the filter is applied on top of the
/// published/public predicate, never instead of it. A filter that matched a Draft and returned it
/// would be the same class of leak as filtering on a Sensitive field, arrived at from the other
/// direction.
/// </remarks>
[Collection("Sequential")]
public class DeliveryFilterIntegrationTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client; // anonymous

    public DeliveryFilterIntegrationTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task SeedAsync(string type)
    {
        using var scope = _factory.Services.CreateScope();
        var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        s.Store(new ContentTypeDefinition
        {
            IsPubliclyDeliverable = true,
            Id = Guid.NewGuid(),
            Name = type,
            DisplayName = type,
            Fields = new()
            {
                new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" },
                new FieldDefinition { Name = "Price", DisplayName = "Price", Type = "number" },
                new FieldDefinition
                {
                    Name = "Cost", DisplayName = "Cost", Type = "number",
                    Sensitivity = SensitivityLevel.Sensitive,
                },
            },
        });

        void Add(string title, double price, double cost, ContentStatus status, SensitivityLevel sens) =>
            s.Store(new Content
            {
                Id = Guid.NewGuid(), ContentType = type, Status = status, Sensitivity = sens,
                Data = new() { ["Title"] = title, ["Price"] = price, ["Cost"] = cost },
            });

        Add("cheap", 100, 10, ContentStatus.Published, SensitivityLevel.Public);
        Add("mid", 500, 50, ContentStatus.Published, SensitivityLevel.Public);
        Add("dear", 900, 90, ContentStatus.Published, SensitivityLevel.Public);

        // Both match a price filter and neither may ever be returned.
        Add("draft-cheap", 100, 10, ContentStatus.Draft, SensitivityLevel.Public);
        Add("sensitive-cheap", 100, 10, ContentStatus.Published, SensitivityLevel.Sensitive);

        await s.SaveChangesAsync();
    }

    private async Task<List<string>> TitlesAsync(string url) => (await PageAsync(url)).Titles;

    private async Task<(List<string> Titles, int Total)> PageAsync(string url)
    {
        var res = await _client.GetAsync(url);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<PagedTitles>();
        // Case-insensitive, because delivery preserves whatever casing a record was stored under
        // and one of these tests stores "title" deliberately.
        return (body!.Items
                .Select(i => i.Data.First(kv =>
                    string.Equals(kv.Key, "Title", StringComparison.OrdinalIgnoreCase)).Value.ToString()!)
                .ToList(),
            body.TotalItems);
    }

    private sealed record Item(Dictionary<string, object> Data);
    private sealed record PagedTitles(List<Item> Items, int TotalItems);

    /// <summary>
    /// A value stored under a differently cased key still filters.
    /// </summary>
    /// <remarks>
    /// ToPublic matches the schema case-insensitively, so a record holding "price" under a schema
    /// field named "Price" is delivered normally. PostgreSQL's jsonb -> is case sensitive, so a
    /// filter built from the schema spelling would miss it: the entry appears in an unfiltered list
    /// and vanishes from a filtered one, which reads as "no matches" rather than as a fault.
    /// </remarks>
    [Fact]
    public async Task A_field_stored_under_different_casing_is_still_filterable()
    {
        var type = "filt_casing";
        using (var scope = _factory.Services.CreateScope())
        {
            var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            s.Store(new ContentTypeDefinition
            {
                IsPubliclyDeliverable = true, Id = Guid.NewGuid(), Name = type, DisplayName = type,
                Fields = new()
                {
                    new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" },
                    new FieldDefinition { Name = "Price", DisplayName = "Price", Type = "number" },
                },
            });
            // Lower-cased keys, which validation accepts because it matches names case-insensitively.
            s.Store(new Content
            {
                Id = Guid.NewGuid(), ContentType = type, Status = ContentStatus.Published,
                Sensitivity = SensitivityLevel.Public,
                Data = new() { ["title"] = "lower", ["price"] = 100d },
            });
            await s.SaveChangesAsync();
        }

        // The control: delivery returns it, so a filter that cannot find it is a fault rather than
        // an honest empty result.
        (await TitlesAsync($"/api/public/{type}")).Should().Equal("lower");

        (await TitlesAsync($"/api/public/{type}?filter[Price][eq]=100"))
            .Should().Equal("lower");
    }

    /// <summary>
    /// A numeric-looking value on a string field compares as a string.
    /// </summary>
    /// <remarks>
    /// jsonb compares by type first, so the stored string "500" never equals the number 500.
    /// Without the schema's declared type, a filter cannot tell which one the caller meant, and
    /// guessing from the text produces a silent empty result.
    /// </remarks>
    [Fact]
    public async Task A_numeric_looking_value_on_a_string_field_still_matches()
    {
        var type = "filt_numeric_string";
        using (var scope = _factory.Services.CreateScope())
        {
            var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            s.Store(new ContentTypeDefinition
            {
                IsPubliclyDeliverable = true, Id = Guid.NewGuid(), Name = type, DisplayName = type,
                Fields = new() { new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" } },
            });
            s.Store(new Content
            {
                Id = Guid.NewGuid(), ContentType = type, Status = ContentStatus.Published,
                Sensitivity = SensitivityLevel.Public,
                Data = new() { ["Title"] = "500" },
            });
            await s.SaveChangesAsync();
        }

        (await TitlesAsync($"/api/public/{type}?filter[Title][eq]=500"))
            .Should().Equal("500");
    }

    /// <summary>
    /// Repeating a filter parameter applies both, and counts both against the cap.
    /// </summary>
    /// <remarks>
    /// StringValues.ToString() joins repeats with a comma, so two parameters became one filter for
    /// the literal value "a,b". That matches nothing and, worse, lets a caller exceed MaxFilters by
    /// repeating one key.
    /// </remarks>
    [Fact]
    public async Task Repeated_filter_parameters_are_not_collapsed_into_one()
    {
        var type = "filt_repeated";
        await SeedAsync(type);

        // The same key twice, which is the case StringValues joins with a comma.
        var titles = await TitlesAsync(
            $"/api/public/{type}?filter[Price][gte]=100&filter[Price][gte]=500");

        titles.Should().BeEquivalentTo(new[] { "mid", "dear" },
            "both repeats must apply, so the stricter bound wins and 100 is excluded");
    }

    [Fact]
    public async Task A_filter_narrows_the_list()
    {
        var type = "filt_narrow";
        await SeedAsync(type);

        var titles = await TitlesAsync($"/api/public/{type}?filter[Price][lte]=500");

        titles.Should().BeEquivalentTo(new[] { "cheap", "mid" });
    }

    /// <summary>
    /// The positive control. Without it, every assertion here would also pass against an endpoint
    /// that returned nothing at all.
    /// </summary>
    [Fact]
    public async Task Without_a_filter_every_published_public_entry_is_returned()
    {
        var type = "filt_control";
        await SeedAsync(type);

        var titles = await TitlesAsync($"/api/public/{type}");

        titles.Should().BeEquivalentTo(new[] { "cheap", "mid", "dear" });
    }

    /// <summary>
    /// The point of the whole feature being in core rather than a module. A filter composes with the
    /// published and sensitivity predicates; it cannot replace them.
    /// </summary>
    [Fact]
    public async Task A_filter_never_returns_a_draft_or_a_sensitive_document()
    {
        var type = "filt_nowiden";
        await SeedAsync(type);

        var (titles, total) = await PageAsync($"/api/public/{type}?filter[Price][eq]=100");

        titles.Should().Equal("cheap");
        titles.Should().NotContain("draft-cheap", "a Draft matches this filter and must stay invisible");
        titles.Should().NotContain("sensitive-cheap", "a Sensitive document matches it too");

        // The count, not just the items. ToPublic() strips a Draft or Sensitive document on the way
        // out, so asserting only on the returned titles passes even with the query's published and
        // sensitivity predicates deleted: the projection hides what the query let through. TotalItems
        // comes from the query itself, before projection, so it is the only assertion here that can
        // actually see whether the database was asked the right question. Verified by mutation.
        total.Should().Be(1,
            "three seeded rows match this filter and the query must exclude two of them itself, "
            + "rather than relying on the projection to hide them afterwards");
    }

    /// <summary>
    /// Sorting is refused rather than ignored, until it is implemented.
    /// </summary>
    /// <remarks>
    /// Accepting the parameter and returning the default order would be a silent wrong answer: the
    /// response looks exactly like a sorted one, and nothing tells the caller otherwise. Tracked
    /// separately from the filtering half of the issue.
    /// </remarks>
    [Theory]
    [InlineData("Price")]
    [InlineData("-Price")]
    public async Task Sorting_is_refused_rather_than_silently_ignored(string sort)
    {
        var type = "filt_sort_" + sort.Replace("-", "desc");
        await SeedAsync(type);

        var res = await _client.GetAsync($"/api/public/{type}?sort={sort}");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Filtering_on_a_sensitive_field_is_refused_with_400()
    {
        var type = "filt_sensitive";
        await SeedAsync(type);

        var res = await _client.GetAsync($"/api/public/{type}?filter[Cost][lte]=50");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "otherwise a caller narrows Cost by watching which entries come back");
    }

    [Fact]
    public async Task An_unknown_field_is_refused_rather_than_ignored()
    {
        var type = "filt_unknown";
        await SeedAsync(type);

        var res = await _client.GetAsync($"/api/public/{type}?filter[Nope][eq]=1");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// A value carrying SQL and JSON syntax is compared, not executed.
    /// </summary>
    /// <remarks>
    /// Field and value both travel as bound parameters, so neither reaches the SQL text. The
    /// matching filter in the same test is the control: without it, this would pass against an
    /// endpoint where Title filtering was broken and every query returned nothing.
    /// </remarks>
    [Theory]
    [InlineData("' OR 1=1 --")]
    [InlineData("\" || @ != \"zzz")]
    [InlineData("cheap' AND (SELECT 1)='1")]
    public async Task A_value_carrying_injection_syntax_matches_nothing(string payload)
    {
        // A distinct type per case: the theory shares a fixture, so re-seeding one name would
        // accumulate rows and the control below would see three "cheap" entries rather than one.
        var type = "filt_inject_" + Math.Abs(payload.GetHashCode());
        await SeedAsync(type);

        var hostile = await TitlesAsync(
            $"/api/public/{type}?filter[Title][eq]={Uri.EscapeDataString(payload)}");
        hostile.Should().BeEmpty("the value is data, so it matches no title");

        var honest = await TitlesAsync($"/api/public/{type}?filter[Title][eq]=cheap");
        honest.Should().BeEquivalentTo(new[] { "cheap" },
            "a real value on the same field still matches, so an empty result above means something");
    }
}
