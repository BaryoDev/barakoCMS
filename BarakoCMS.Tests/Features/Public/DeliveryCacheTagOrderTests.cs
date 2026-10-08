using System.Net;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Infrastructure.Caching;
using barakoCMS.Models;
using BarakoCMS.Tests.Features.Site;

namespace BarakoCMS.Tests.Features.Public;

/// <summary>
/// Which tags survive the bound, and what a read that is not the file sends: type tags before entry
/// tags so an included type is still purged, the sitemap tag first on a tenant with many types, and
/// no validators on a file 404.
/// </summary>
[Collection("Sequential")]
public class DeliveryCacheTagOrderTests(IntegrationTestFixture fixture)
{
    private readonly ShareLinkTestHost _host = new(fixture);

    private static CancellationToken Ct => ShareLinkTestHost.Ct;

    private static string[] Tags(HttpResponseMessage response) =>
        response.Headers.TryGetValues(DeliveryCache.SurrogateKeyHeader, out var values)
            ? values.SelectMany(v => v.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToArray()
            : [];

    private static ContentTypeDefinition Type(string name, params FieldDefinition[] fields) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        DisplayName = name,
        IsPubliclyDeliverable = true,
        Fields = [new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" }, .. fields],
    };

    private static Content Published(string type, Dictionary<string, object> data) => new()
    {
        Id = Guid.NewGuid(),
        ContentType = type,
        Status = ContentStatus.Published,
        Sensitivity = SensitivityLevel.Public,
        Data = data,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task A_page_of_twenty_with_includes_keeps_the_included_type_tag_ahead_of_the_entry_tags()
    {
        var tenant = await _host.TenantAsync();
        var post = $"post{Guid.NewGuid():N}"[..12];
        var author = $"auth{Guid.NewGuid():N}"[..12];
        var store = fixture.Services.GetRequiredService<IDocumentStore>();
        await using (var session = store.LightweightSession(tenant))
        {
            session.Store(Type(author));
            session.Store(Type(post,
                new FieldDefinition { Name = "Slug", DisplayName = "Slug", Type = "slug" },
                new FieldDefinition { Name = "Author", DisplayName = "Author", Type = "reference", ReferenceType = author }));
            for (var i = 0; i < 20; i++)
            {
                var writer = Published(author, new() { ["Title"] = $"writer-{i}" });
                session.Store(writer);
                session.Store(Published(post, new() { ["Title"] = $"post-{i}", ["Slug"] = $"post-{i}", ["Author"] = writer.Id.ToString() }));
            }
            await session.SaveChangesAsync(Ct);
        }

        var included = await _host.AnonymousIn(tenant).GetAsync($"/api/public/{post}?include=Author", Ct);
        included.StatusCode.Should().Be(HttpStatusCode.OK, await included.Content.ReadAsStringAsync(Ct));
        (await included.Content.ReadAsStringAsync(Ct)).Should().Contain("writer-", "the positive control: the authors were included");

        var tags = Tags(included);
        tags.Should().HaveCountGreaterThan(3);
        tags.Take(3).Should().Equal($"t:{tenant}", $"t:{tenant}:type:{post}", $"t:{tenant}:type:{author}");
        included.Headers.GetValues(DeliveryCache.TagsDroppedHeader).Should().ContainSingle()
            .Which.Should().NotBe("0", "twenty posts and twenty authors are more entry tags than the bound holds");

        var referenced = await _host.AnonymousIn(tenant).GetAsync($"/api/public/{post}", Ct);
        referenced.StatusCode.Should().Be(HttpStatusCode.OK);
        Tags(referenced).Should().NotBeEmpty().And.Contain($"t:{tenant}:type:{author}",
            "a referenced type is tagged even when it is not included, since its entries decide which ids are served");
    }

    [Fact]
    public async Task A_sitemap_over_forty_types_keeps_its_own_tag_first_and_drops_type_tags_past_the_bound()
    {
        var tenant = await _host.TenantAsync();
        var store = fixture.Services.GetRequiredService<IDocumentStore>();
        await using (var session = store.LightweightSession(tenant))
        {
            for (var i = 0; i < 40; i++)
                session.Store(Type($"map{i:D2}{Guid.NewGuid():N}"[..14]));
            await session.SaveChangesAsync(Ct);
        }

        var sitemap = await _host.AnonymousIn(tenant).GetAsync("/api/public/sitemap.xml", Ct);
        sitemap.StatusCode.Should().Be(HttpStatusCode.OK, await sitemap.Content.ReadAsStringAsync(Ct));

        var tags = Tags(sitemap);
        tags.Should().HaveCountGreaterThan(2);
        tags.Length.Should().BeLessThanOrEqualTo(DeliveryCache.MaxTags);
        tags.Take(2).Should().Equal($"t:{tenant}", $"t:{tenant}:sitemap");
        sitemap.Headers.GetValues(DeliveryCache.TagsDroppedHeader).Should().ContainSingle()
            .Which.Should().NotBe("0", "forty type tags do not fit, and the sitemap tag is what a purge relies on");
    }

    [Fact]
    public async Task A_public_file_whose_bytes_are_missing_answers_404_with_no_validator_class_or_long_max_age()
    {
        var id = Guid.NewGuid();
        using (var scope = fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new BarakoCMS.Files.StoredFile
            {
                Id = id,
                FileName = "gone.png",
                ContentType = "image/png",
                Size = 10,
                StorageKey = $"missing/{Guid.NewGuid():N}",
                IsPublic = true,
            });
            await session.SaveChangesAsync(Ct);
        }

        var response = await fixture.CreateClient().GetAsync($"/api/public/files/{id}", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Headers.CacheControl!.NoStore.Should().BeTrue("a missing object must not be kept for a day");
        response.Headers.Contains("ETag").Should().BeFalse();
        response.Headers.Contains(DeliveryCache.ClassHeader).Should().BeFalse();
        response.Headers.Contains(DeliveryCache.SurrogateKeyHeader).Should().BeFalse();
        response.Headers.Contains(DeliveryCache.CacheTagHeader).Should().BeFalse();
        response.Content.Headers.LastModified.Should().BeNull();
    }
}
