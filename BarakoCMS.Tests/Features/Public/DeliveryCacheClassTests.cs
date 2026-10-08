using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Infrastructure.Caching;
using barakoCMS.Models;
using BarakoCMS.Tests.Features.Site;

namespace BarakoCMS.Tests.Features.Public;

/// <summary>
/// Every delivery read says its cache class beside an unchanged Cache-Control, carries a weak ETag
/// and tenant tags, and answers 304 with no body when the caller already holds it (#973, #561).
/// </summary>
[Collection("Sequential")]
public class DeliveryCacheClassTests(IntegrationTestFixture fixture)
{
    private readonly ShareLinkTestHost _host = new(fixture);

    private static CancellationToken Ct => ShareLinkTestHost.Ct;

    private static string[] Tags(HttpResponseMessage response, string header = DeliveryCache.SurrogateKeyHeader) =>
        response.Headers.TryGetValues(header, out var values)
            ? values.SelectMany(v => v.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToArray()
            : [];

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;

    private static string ETagOf(HttpResponseMessage response) =>
        response.Headers.GetValues("ETag").Should().ContainSingle().Subject;

    /// <summary>A null tenant sends no X-Tenant, so the request is the default tenant's by host.</summary>
    private async Task<HttpResponseMessage> GetAsync(
        string? tenant, string url, string? ifNoneMatch = null, DateTimeOffset? ifModifiedSince = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (ifNoneMatch is not null)
            request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        if (ifModifiedSince is { } since)
            request.Headers.IfModifiedSince = since;
        var client = tenant is null ? fixture.CreateClient() : _host.AnonymousIn(tenant);
        return await client.SendAsync(request, Ct);
    }

    private async Task<Guid> PublishedAsync(string tenant, string type, string slug, string marker)
    {
        var id = Guid.NewGuid();
        var store = fixture.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession(tenant);
        session.Store(new Content
        {
            Id = id,
            ContentType = type,
            Status = ContentStatus.Published,
            Sensitivity = SensitivityLevel.Public,
            Data = new Dictionary<string, object> { ["Title"] = marker, ["Slug"] = slug, ["Body"] = marker },
            SearchText = marker,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await session.SaveChangesAsync(Ct);
        return id;
    }

    [Fact]
    public async Task Every_delivery_route_answers_its_class_a_weak_etag_and_tags_under_its_tenant_and_304_on_a_match()
    {
        var tenant = await _host.TenantAsync();
        var type = await _host.TypeAsync(tenant);
        var marker = $"MARK{Guid.NewGuid():N}";
        var entryId = await PublishedAsync(tenant, type, "the-entry", marker);
        var from = $"/moved-{Guid.NewGuid():N}";
        var store = fixture.Services.GetRequiredService<IDocumentStore>();
        await using (var session = store.LightweightSession(tenant))
        {
            session.Store(new UrlRedirect { Id = Guid.NewGuid(), FromPath = from, ToPath = "/here", Permanent = true });
            await session.SaveChangesAsync(Ct);
        }

        var typeTag = $"t:{tenant}:type:{type}";
        var entryTag = $"t:{tenant}:entry:{entryId:D}";
        var routes = new (string Url, string? CacheControl, string[] Tags)[]
        {
            ($"/api/public/{type}", "public, max-age=60", [typeTag, entryTag]),
            ($"/api/public/{type}/the-entry", "public, max-age=60", [typeTag, entryTag]),
            ($"/api/public/{type}/search?q={marker}", "public, max-age=60", [typeTag, entryTag]),
            ($"/api/public/{type}/search?q=a", "public, max-age=60", [typeTag]),
            ($"/api/public/{type}/feed.xml", "public, max-age=60", [typeTag, entryTag]),
            ("/api/public/sitemap.xml", "public, max-age=60", [$"t:{tenant}:sitemap", typeTag]),
            ($"/api/public/types/{type}/description", "public, max-age=60", [typeTag]),
            ($"/api/public/redirects/resolve?path={Uri.EscapeDataString(from)}", null, [$"t:{tenant}:redirects"]),
        };

        foreach (var (url, cacheControl, expected) in routes)
        {
            var first = await GetAsync(tenant, url);
            first.StatusCode.Should().Be(HttpStatusCode.OK, $"{url}: {await first.Content.ReadAsStringAsync(Ct)}");

            Header(first, DeliveryCache.ClassHeader).Should().Be("short", url);
            (first.Headers.CacheControl?.ToString()).Should().Be(cacheControl,
                $"{url} keeps the Cache-Control it had, so nothing is cached longer than before");

            var etag = ETagOf(first);
            etag.Should().StartWith("W/\"", $"{url} sends a weak tag");

            var tags = Tags(first);
            tags.Should().NotBeEmpty(url);
            tags.Should().Contain($"t:{tenant}").And.Contain(expected, url);
            tags.Should().OnlyContain(t => t == $"t:{tenant}" || t.StartsWith($"t:{tenant}:"), url);
            Tags(first, DeliveryCache.CacheTagHeader).Should().Equal(tags, "both spellings carry one value");

            var again = await GetAsync(tenant, url, ifNoneMatch: etag);
            again.StatusCode.Should().Be(HttpStatusCode.NotModified, url);
            (await again.Content.ReadAsByteArrayAsync(Ct)).Should().BeEmpty($"{url} sends no body on a 304");

            var other = await GetAsync(tenant, url, ifNoneMatch: "W/\"something-else\"");
            other.StatusCode.Should().Be(HttpStatusCode.OK, $"{url} answers in full when the tag does not match");
        }
    }

    [Fact]
    public async Task A_change_to_the_entry_changes_the_etag_and_the_old_tag_gets_the_new_body()
    {
        var tenant = await _host.TenantAsync();
        var type = await _host.TypeAsync(tenant);
        var id = await PublishedAsync(tenant, type, "changing", "before-the-edit");

        var first = await GetAsync(tenant, $"/api/public/{type}/changing");
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var oldTag = ETagOf(first);
        var oldList = ETagOf(await GetAsync(tenant, $"/api/public/{type}"));

        var store = fixture.Services.GetRequiredService<IDocumentStore>();
        await using (var session = store.LightweightSession(tenant))
        {
            var entry = await session.LoadAsync<Content>(id, Ct);
            entry!.Data["Title"] = "after-the-edit";
            entry.UpdatedAt = DateTime.UtcNow;
            session.Store(entry);
            await session.SaveChangesAsync(Ct);
        }

        var after = await GetAsync(tenant, $"/api/public/{type}/changing", ifNoneMatch: oldTag);
        after.StatusCode.Should().Be(HttpStatusCode.OK, "the caller's copy is out of date");
        (await after.Content.ReadAsStringAsync(Ct)).Should().Contain("after-the-edit");
        ETagOf(after).Should().NotBe(oldTag);

        var list = await GetAsync(tenant, $"/api/public/{type}", ifNoneMatch: oldList);
        list.StatusCode.Should().Be(HttpStatusCode.OK, "the list holds the edited entry too");
        ETagOf(list).Should().NotBe(oldList);
    }

    [Fact]
    public async Task A_slug_read_sends_no_last_modified_so_if_modified_since_alone_never_answers_304()
    {
        var tenant = await _host.TenantAsync();
        var type = await _host.TypeAsync(tenant);
        await PublishedAsync(tenant, type, "dated", "dated-entry");

        var first = await GetAsync(tenant, $"/api/public/{type}/dated");
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        first.Content.Headers.LastModified.Should().BeNull(
            "a referenced entry or a file can change the body without moving the entry's timestamp");

        var later = DateTimeOffset.UtcNow.AddDays(1);
        (await GetAsync(tenant, $"/api/public/{type}/dated", ifModifiedSince: later)).StatusCode
            .Should().Be(HttpStatusCode.OK, "with no Last-Modified sent, If-Modified-Since is ignored");
        (await GetAsync(tenant, $"/api/public/{type}", ifModifiedSince: later)).StatusCode
            .Should().Be(HttpStatusCode.OK, "a list has no Last-Modified either");

        var description = await GetAsync(tenant, $"/api/public/types/{type}/description");
        description.StatusCode.Should().Be(HttpStatusCode.OK);
        var lastModified = description.Content.Headers.LastModified;
        lastModified.Should().NotBeNull("the type's own timestamp covers everything the description sends");
        (await GetAsync(tenant, $"/api/public/types/{type}/description", ifModifiedSince: lastModified)).StatusCode
            .Should().Be(HttpStatusCode.NotModified);
        (await GetAsync(tenant, $"/api/public/types/{type}/description", ifModifiedSince: lastModified!.Value.AddDays(-1))).StatusCode
            .Should().Be(HttpStatusCode.OK, "the type changed after that date");
        (await GetAsync(tenant, $"/api/public/types/{type}/description", ifNoneMatch: "W/\"stale\"", ifModifiedSince: lastModified)).StatusCode
            .Should().Be(HttpStatusCode.OK, "If-None-Match decides when both are sent");
    }

    [Fact]
    public async Task Two_tenants_on_one_url_get_different_etags_and_never_each_others_tags()
    {
        var tenantA = await _host.TenantAsync();
        var tenantB = await _host.TenantAsync();
        var type = $"same{Guid.NewGuid():N}"[..12];
        var store = fixture.Services.GetRequiredService<IDocumentStore>();
        foreach (var tenant in new[] { tenantA, tenantB })
        {
            await using var session = store.LightweightSession(tenant);
            session.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(),
                Name = type,
                DisplayName = type,
                IsPubliclyDeliverable = true,
                Fields = [new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" }],
            });
            await session.SaveChangesAsync(Ct);
        }

        // Both lists are empty, so the two bodies are byte for byte the same.
        var a = await GetAsync(tenantA, $"/api/public/{type}");
        var b = await GetAsync(tenantB, $"/api/public/{type}");
        a.StatusCode.Should().Be(HttpStatusCode.OK);
        b.StatusCode.Should().Be(HttpStatusCode.OK);
        (await a.Content.ReadAsStringAsync(Ct)).Should().Be(await b.Content.ReadAsStringAsync(Ct));

        ETagOf(a).Should().NotBe(ETagOf(b), "the tenant is part of the tag");
        (await GetAsync(tenantB, $"/api/public/{type}", ifNoneMatch: ETagOf(a))).StatusCode
            .Should().Be(HttpStatusCode.OK, "tenant A's tag does not revalidate tenant B's copy");

        var tagsA = Tags(a);
        tagsA.Should().HaveCount(2);
        tagsA.Should().OnlyContain(t => t.StartsWith($"t:{tenantA}"));
        tagsA.Should().NotContain(t => t.Contains(tenantB));
        Tags(b).Should().HaveCount(2).And.OnlyContain(t => t.StartsWith($"t:{tenantB}"));
    }

    [Fact]
    public async Task Preview_reads_by_jwt_and_by_share_link_are_no_store_with_no_shared_cache_tags()
    {
        var tenant = await _host.TenantAsync();
        var type = await _host.TypeAsync(tenant);
        var draftId = await _host.EntryAsync(tenant, type, "wip");
        var admin = await _host.SuperAdminInAsync(tenant);
        var mint = await admin.PostAsJsonAsync("/api/preview", new { Type = type, Slug = "wip" }, Ct);
        mint.StatusCode.Should().Be(HttpStatusCode.OK, await mint.Content.ReadAsStringAsync(Ct));
        var shareKey = (await mint.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("token").GetString()!;
        var config = fixture.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
        var jwt = barakoCMS.Infrastructure.Preview.PreviewToken.Create(config, tenant, type, "wip", draftId).Token;

        foreach (var token in new[] { shareKey, jwt })
        {
            var url = $"/api/public/{type}/wip?preview={Uri.EscapeDataString(token)}";
            var preview = await GetAsync(tenant, url, ifNoneMatch: "*");

            preview.StatusCode.Should().Be(HttpStatusCode.OK, "a preview is never answered from a validator");
            (await preview.Content.ReadAsStringAsync(Ct)).Should().Contain(ShareLinkTestHost.BodyOf("wip"));
            preview.Headers.CacheControl!.NoStore.Should().BeTrue();
            Header(preview, DeliveryCache.ClassHeader).Should().Be("no-store");
            Tags(preview).Should().BeEmpty("a draft gives a shared cache nothing to file it under");
            Tags(preview, DeliveryCache.CacheTagHeader).Should().BeEmpty();
            preview.Headers.Contains("ETag").Should().BeFalse();
            preview.Content.Headers.LastModified.Should().BeNull();
        }
    }

    [Fact]
    public async Task A_public_file_is_long_with_a_file_tag_and_answers_304_without_reading_the_bytes_again()
    {
        var token = await fixture.StoredUserTokenAsync("SuperAdmin");
        using var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(FileSamples.Png(4, 7, 3));
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(content, "file", "pic.png");
        form.Add(new StringContent("true"), "isPublic");
        using var upload = new HttpRequestMessage(HttpMethod.Post, "/api/files") { Content = form };
        upload.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var uploaded = await fixture.CreateClient().SendAsync(upload, Ct);
        uploaded.StatusCode.Should().Be(HttpStatusCode.Created, await uploaded.Content.ReadAsStringAsync(Ct));
        var id = (await uploaded.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();

        var tenant = barakoCMS.Models.Tenant.DefaultSlug;
        var first = await GetAsync(null, $"/api/public/files/{id}");
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        first.Headers.CacheControl!.ToString().Should().Be("public, max-age=86400");
        Header(first, DeliveryCache.ClassHeader).Should().Be("long");
        first.Content.Headers.LastModified.Should().NotBeNull();
        Tags(first).Should().HaveCount(2).And.Contain($"t:{tenant}:file:{id:D}");
        var etag = ETagOf(first);

        var again = await GetAsync(null, $"/api/public/files/{id}", ifNoneMatch: etag);
        again.StatusCode.Should().Be(HttpStatusCode.NotModified);
        (await again.Content.ReadAsByteArrayAsync(Ct)).Should().BeEmpty();

        var meta = await GetAsync(null, $"/api/public/files/{id}/meta");
        meta.StatusCode.Should().Be(HttpStatusCode.OK);
        Header(meta, DeliveryCache.ClassHeader).Should().Be("short");
        meta.Headers.CacheControl!.ToString().Should().Be("public, max-age=300");
        (await GetAsync(null, $"/api/public/files/{id}/meta", ifNoneMatch: ETagOf(meta))).StatusCode
            .Should().Be(HttpStatusCode.NotModified);
    }

    [Fact]
    public async Task Semantic_search_is_short_with_its_hits_tagged_and_answers_304_on_a_match()
    {
        var tenant = barakoCMS.Models.Tenant.DefaultSlug;
        var type = $"aicache{Guid.NewGuid():N}"[..14];
        Guid solar;
        using (var scope = fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new ContentTypeDefinition
            {
                IsPubliclyDeliverable = true,
                Id = Guid.NewGuid(), Name = type, DisplayName = type,
                Fields =
                [
                    new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" },
                    new FieldDefinition { Name = "Slug", DisplayName = "Slug", Type = "slug" },
                    new FieldDefinition { Name = "Body", DisplayName = "Body", Type = "markdown" },
                ],
            });
            solar = Guid.NewGuid();
            session.Store(new Content
            {
                Id = solar, ContentType = type, Status = ContentStatus.Published, Sensitivity = SensitivityLevel.Public,
                Data = new() { ["Title"] = "Solar panels", ["Slug"] = "solar", ["Body"] = "photovoltaic renewable sunlight energy generation" },
            });
            await session.SaveChangesAsync(Ct);
        }

        var admin = fixture.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await fixture.StoredUserTokenAsync("SuperAdmin"));
        (await admin.PostAsync($"/api/ai/index/{type}", null, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var url = $"/api/public/{type}/semantic?q=photovoltaic%20renewable%20sunlight";
        var first = await GetAsync(null, url);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        (await first.Content.ReadAsStringAsync(Ct)).Should().Contain("\"solar\"", "the positive control: the hit is in the body");
        Header(first, DeliveryCache.ClassHeader).Should().Be("short");
        first.Headers.CacheControl!.ToString().Should().Be("public, max-age=60");
        Tags(first).Should().HaveCount(3).And.Contain(new[] { $"t:{tenant}:type:{type}", $"t:{tenant}:entry:{solar:D}" });

        (await GetAsync(null, url, ifNoneMatch: ETagOf(first))).StatusCode.Should().Be(HttpStatusCode.NotModified);
    }
}
