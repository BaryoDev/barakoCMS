using Xunit;
using FluentAssertions;
using System.Net;
using System.Net.Http.Json;
using barakoCMS.Models;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests;

/// <summary>
/// The public delivery API end to end against the real API over a real Postgres, with an ANONYMOUS
/// client (no auth header). Adversarial: a draft is never exposed, a document-level Sensitive entry is
/// never exposed, a Sensitive field's value never leaks, and a slug doesn't resolve across types.
/// </summary>
[Collection("Sequential")]
public class PublicDeliveryTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client; // no Authorization header -> anonymous

    public PublicDeliveryTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private const string Secret = "topsecret-value-12345";

    // A "post" type with a slug field and a document plus a Sensitive field, stored in the default tenant.
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
                new FieldDefinition { Name = "Slug", DisplayName = "Slug", Type = "slug" },
                new FieldDefinition { Name = "Secret", DisplayName = "Secret", Type = "string", Sensitivity = SensitivityLevel.Sensitive },
            },
        });

        s.Store(new Content
        {
            Id = Guid.NewGuid(), ContentType = type, Status = ContentStatus.Published, Sensitivity = SensitivityLevel.Public,
            Data = new()
            {
                ["Title"] = "Hello World",
                ["Slug"] = "hello-world",
                ["Secret"] = Secret,                       /* Sensitive field, correct case */
                ["SECRET"] = "cased-secret-leak-abc",       /* Sensitive value under a mis-cased key */
                ["OrphanNote"] = "orphan-leak-xyz",         /* key with no field in the schema at all */
            },
        });
        s.Store(new Content
        {
            Id = Guid.NewGuid(), ContentType = type, Status = ContentStatus.Draft, Sensitivity = SensitivityLevel.Public,
            Data = new() { ["Title"] = "A Draft", ["Slug"] = "draft-post" },
        });
        s.Store(new Content
        {
            Id = Guid.NewGuid(), ContentType = type, Status = ContentStatus.Published, Sensitivity = SensitivityLevel.Sensitive,
            Data = new() { ["Title"] = "Secret Post", ["Slug"] = "secret-post" },
        });
        await s.SaveChangesAsync();
    }

    [Fact]
    public async Task List_ReturnsOnlyPublishedNonSensitive_Anonymously()
    {
        var type = "postpub_list";
        await SeedAsync(type);

        var res = await _client.GetAsync($"/api/public/{type}");
        res.StatusCode.Should().Be(HttpStatusCode.OK, because: "delivery is anonymous");
        var body = await res.Content.ReadAsStringAsync();

        body.Should().Contain("hello-world", "the published post is public");
        body.Should().NotContain("draft-post", "drafts must never be exposed");
        body.Should().NotContain("secret-post", "a document-level Sensitive entry is not public");
        body.Should().NotContain(Secret, "a Sensitive field's value must never leak");
        res.Headers.CacheControl?.Public.Should().BeTrue("public reads are CDN-cacheable");
    }

    [Fact]
    public async Task PublicPayload_ExposesOnlyAllowlistedPublicFields()
    {
        /* Guards the security-review findings: a Sensitive field's value (any casing), and an orphan
         * key with no schema field, must never reach an anonymous caller. Only Public schema fields
         * are exposed. */
        var type = "postpub_allowlist";
        await SeedAsync(type);

        var res = await _client.GetAsync($"/api/public/{type}/hello-world");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadAsStringAsync();

        body.Should().Contain("Hello World", "Title is a Public field");
        body.Should().NotContain(Secret, "the Sensitive field value must not leak");
        body.Should().NotContain("cased-secret-leak-abc", "a Sensitive value under a mis-cased key must not leak");
        body.Should().NotContain("orphan-leak-xyz", "a key with no Public schema field must not leak");
    }

    [Fact]
    public async Task GetBySlug_ReturnsPublishedEntry_WithSensitiveFieldMasked()
    {
        var type = "postpub_slug";
        await SeedAsync(type);

        var res = await _client.GetAsync($"/api/public/{type}/hello-world");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadAsStringAsync();
        body.Should().Contain("Hello World");
        body.Should().NotContain(Secret, "the Sensitive field is masked for anonymous callers");
    }

    [Fact]
    public async Task GetBySlug_Draft_Is404()
    {
        var type = "postpub_draft";
        await SeedAsync(type);
        var res = await _client.GetAsync($"/api/public/{type}/draft-post");
        res.StatusCode.Should().Be(HttpStatusCode.NotFound, "a draft is not addressable publicly");
    }

    [Fact]
    public async Task GetBySlug_DocumentSensitive_Is404()
    {
        var type = "postpub_sens";
        await SeedAsync(type);
        var res = await _client.GetAsync($"/api/public/{type}/secret-post");
        res.StatusCode.Should().Be(HttpStatusCode.NotFound, "a published-but-Sensitive doc is not public");
    }

    [Fact]
    public async Task GetBySlug_Missing_Is404()
    {
        var type = "postpub_missing";
        await SeedAsync(type);
        var res = await _client.GetAsync($"/api/public/{type}/no-such-slug");
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetBySlug_DoesNotResolveAcrossTypes()
    {
        // "hello-world" exists under postpub_a. Type postpub_b is slug-addressable but has a different
        // entry, so a query for "hello-world" under postpub_b must not resolve.
        await SeedAsync("postpub_a");

        using (var scope = _factory.Services.CreateScope())
        {
            var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            s.Store(new ContentTypeDefinition
            {
            IsPubliclyDeliverable = true,
                Id = Guid.NewGuid(), Name = "postpub_b", DisplayName = "b",
                Fields = new() { new FieldDefinition { Name = "Slug", DisplayName = "Slug", Type = "slug" } },
            });
            s.Store(new Content
            {
                Id = Guid.NewGuid(), ContentType = "postpub_b", Status = ContentStatus.Published,
                Sensitivity = SensitivityLevel.Public, Data = new() { ["Slug"] = "different-post" },
            });
            await s.SaveChangesAsync();
        }

        var res = await _client.GetAsync("/api/public/postpub_b/hello-world");
        res.StatusCode.Should().Be(HttpStatusCode.NotFound, "a slug is scoped to its own content type");
    }
}
