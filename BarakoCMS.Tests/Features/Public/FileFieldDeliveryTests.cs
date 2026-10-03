using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using barakoCMS.Core.Interfaces;
using barakoCMS.Models;
using BarakoCMS.Files;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Public;

/// <summary>
/// What anonymous delivery answers for a <c>file</c> field, over HTTP with the Files module behind
/// the seam: the public file as an object, and nothing at all for any other file.
/// </summary>
/// <remarks>
/// Every type is unique to its test and every entry is found by its own title, because the
/// database is shared. Entries are stored directly, as published ones, so what is under test is the
/// read: the write rules have their own class.
/// </remarks>
[Collection("Sequential")]
public class FileFieldDeliveryTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _anonymous;

    public FileFieldDeliveryTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _anonymous = factory.CreateClient();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewName(string prefix) => $"{prefix}-{Guid.NewGuid():n}"[..(prefix.Length + 13)];

    private static FieldDefinition Field(string name, string type, string? referenceType = null) =>
        new() { Name = name, DisplayName = name, Type = type, ReferenceType = referenceType };

    private async Task<string> StoreTypeAsync(string prefix, params FieldDefinition[] fields)
    {
        var name = NewName(prefix);
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = name,
            DisplayName = name,
            IsPubliclyDeliverable = true,
            Fields = [Field("Title", "string"), .. fields],
        });
        await session.SaveChangesAsync(Ct);
        return name;
    }

    private async Task<Guid> StoreEntryAsync(string type, string title, Dictionary<string, object> data)
    {
        var entry = new Content
        {
            Id = Guid.NewGuid(),
            ContentType = type,
            Status = ContentStatus.Published,
            Sensitivity = SensitivityLevel.Public,
            Data = new Dictionary<string, object>(data) { ["Title"] = title },
            SearchText = title,
        };

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(entry);
        await session.SaveChangesAsync(Ct);
        return entry.Id;
    }

    private async Task<Guid> FileAsync(bool isPublic, string name, string? alt = null, string? caption = null)
    {
        Guid id;
        using (var scope = _factory.Services.CreateScope())
        {
            using var content = new MemoryStream(FileSamples.Png(Guid.NewGuid().ToByteArray()));
            var saved = await scope.ServiceProvider.GetRequiredService<IFileStore>().SaveAsync(
                new FileToStore { Content = content, FileName = name, ContentType = "image/png", IsPublic = isPublic, Owner = Guid.NewGuid() },
                Ct);

            saved.File.Should().NotBeNull(saved.Refused ?? string.Empty);
            id = saved.File!.Id;
        }

        if (alt is not null || caption is not null)
        {
            using var scope = _factory.Services.CreateScope();
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var record = await session.LoadAsync<StoredFile>(id, Ct);
            record.Should().NotBeNull();
            record!.Alt = alt;
            record.Caption = caption;
            session.Store(record);
            await session.SaveChangesAsync(Ct);
        }

        return id;
    }

    private async Task<(string Body, List<JsonElement> Items)> ListAsync(string type, string query = "")
    {
        var response = await _anonymous.GetAsync($"/api/public/{type}?pageSize=100{query}", Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        var items = JsonDocument.Parse(body).RootElement.GetProperty("items").EnumerateArray().Select(i => i.Clone()).ToList();
        return (body, items);
    }

    private static JsonElement DataOf(IEnumerable<JsonElement> items, string title) =>
        items.Single(i => i.GetProperty("data").GetProperty("Title").GetString() == title).GetProperty("data");

    private static void ShouldBeTheFile(JsonElement cover, Guid id, string fileName)
    {
        cover.ValueKind.Should().Be(JsonValueKind.Object, "delivery answers the file, not its id");
        cover.GetProperty("id").GetGuid().Should().Be(id);
        cover.GetProperty("url").GetString().Should().Be($"/api/public/files/{id}");
        cover.GetProperty("fileName").GetString().Should().Be(fileName);
        cover.GetProperty("contentType").GetString().Should().Be("image/png");
        cover.GetProperty("size").GetInt64().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task A_public_file_is_answered_as_an_object_on_the_list_the_slug_route_and_search()
    {
        var type = await StoreTypeAsync("fdel", Field("Slug", "slug"), Field("Cover", "file"));
        var cover = await FileAsync(isPublic: true, "harbour.png", alt: "Boats at dawn", caption: "Taken in March");
        var needle = "needle" + Guid.NewGuid().ToString("n")[..10];
        await StoreEntryAsync(type, needle, new() { ["Slug"] = "the-harbour", ["Cover"] = cover.ToString() });

        var (_, items) = await ListAsync(type);
        items.Should().ContainSingle();
        var listed = DataOf(items, needle).GetProperty("Cover");
        ShouldBeTheFile(listed, cover, "harbour.png");
        listed.GetProperty("alt").GetString().Should().Be("Boats at dawn");
        listed.GetProperty("caption").GetString().Should().Be("Taken in March");

        // The address is one an anonymous reader can fetch.
        var fetched = await _anonymous.GetAsync(listed.GetProperty("url").GetString(), Ct);
        fetched.StatusCode.Should().Be(HttpStatusCode.OK);

        var bySlug = await _anonymous.GetAsync($"/api/public/{type}/the-harbour", Ct);
        bySlug.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var doc = JsonDocument.Parse(await bySlug.Content.ReadAsStringAsync(Ct)))
        {
            ShouldBeTheFile(doc.RootElement.GetProperty("data").GetProperty("Cover"), cover, "harbour.png");
        }

        var search = await _anonymous.GetAsync($"/api/public/{type}/search?q={needle}", Ct);
        search.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var doc = JsonDocument.Parse(await search.Content.ReadAsStringAsync(Ct)))
        {
            var results = doc.RootElement.GetProperty("results").EnumerateArray().ToList();
            results.Should().ContainSingle();
            ShouldBeTheFile(results[0].GetProperty("data").GetProperty("Cover"), cover, "harbour.png");
        }
    }

    [Fact]
    public async Task A_private_file_a_missing_file_and_text_are_left_out_and_each_entry_still_reads()
    {
        var type = await StoreTypeAsync("fdel", Field("Slug", "slug"), Field("Cover", "file"));
        var open = await FileAsync(isPublic: true, "open.png");
        var locked = await FileAsync(isPublic: false, "payslip-march.png", alt: "Net pay for March");
        var missing = Guid.NewGuid();

        await StoreEntryAsync(type, "public", new() { ["Slug"] = "public", ["Cover"] = open.ToString() });
        await StoreEntryAsync(type, "private", new() { ["Slug"] = "private", ["Cover"] = locked.ToString() });
        await StoreEntryAsync(type, "missing", new() { ["Slug"] = "missing", ["Cover"] = missing.ToString() });
        await StoreEntryAsync(type, "text", new() { ["Slug"] = "text", ["Cover"] = "https://cdn.example.com/cover.png" });

        var (body, items) = await ListAsync(type);

        items.Should().HaveCount(4, "an entry whose file does not resolve is still an entry");
        ShouldBeTheFile(DataOf(items, "public").GetProperty("Cover"), open, "open.png");

        foreach (var title in new[] { "private", "missing", "text" })
        {
            DataOf(items, title).TryGetProperty("Cover", out _).Should().BeFalse(
                "the '{0}' entry names nothing an anonymous reader may have", title);
        }

        body.Should().NotContain(locked.ToString(), "not even the id of a private file is answered");
        body.Should().NotContain("payslip").And.NotContain("Net pay");
        body.Should().NotContain(missing.ToString());

        var bySlug = await _anonymous.GetAsync($"/api/public/{type}/private", Ct);
        var single = await bySlug.Content.ReadAsStringAsync(Ct);
        bySlug.StatusCode.Should().Be(HttpStatusCode.OK, single);
        single.Should().NotContain(locked.ToString()).And.NotContain("payslip");
        using var doc = JsonDocument.Parse(single);
        doc.RootElement.GetProperty("data").GetProperty("Title").GetString().Should().Be("private");
        doc.RootElement.GetProperty("data").TryGetProperty("Cover", out _).Should().BeFalse();
    }

    [Fact]
    public async Task A_file_field_the_type_does_not_mark_public_is_not_delivered()
    {
        var hidden = Field("Scan", "file");
        hidden.Sensitivity = SensitivityLevel.Sensitive;
        var type = await StoreTypeAsync("fdel", hidden);
        var open = await FileAsync(isPublic: true, "scan.png");
        await StoreEntryAsync(type, "masked", new() { ["Scan"] = open.ToString() });

        var (body, items) = await ListAsync(type);

        items.Should().ContainSingle();
        DataOf(items, "masked").TryGetProperty("Scan", out _).Should().BeFalse();
        body.Should().NotContain(open.ToString()).And.NotContain("scan.png");
    }

    [Fact]
    public async Task An_included_entry_answers_its_own_file_fields_the_same_way()
    {
        var author = await StoreTypeAsync("fauthor", Field("Photo", "file"));
        var post = await StoreTypeAsync("fpost", Field("Author", "reference", referenceType: author));

        var open = await FileAsync(isPublic: true, "portrait.png");
        var locked = await FileAsync(isPublic: false, "passport.png");

        var shown = await StoreEntryAsync(author, "Ada", new() { ["Photo"] = open.ToString() });
        var withheld = await StoreEntryAsync(author, "Grace", new() { ["Photo"] = locked.ToString() });
        await StoreEntryAsync(post, "by Ada", new() { ["Author"] = shown.ToString() });
        await StoreEntryAsync(post, "by Grace", new() { ["Author"] = withheld.ToString() });

        var (body, items) = await ListAsync(post, "&include=Author");

        items.Should().HaveCount(2);

        var ada = DataOf(items, "by Ada").GetProperty("Author").GetProperty("data");
        ada.GetProperty("Title").GetString().Should().Be("Ada");
        ShouldBeTheFile(ada.GetProperty("Photo"), open, "portrait.png");

        var grace = DataOf(items, "by Grace").GetProperty("Author").GetProperty("data");
        grace.GetProperty("Title").GetString().Should().Be("Grace");
        grace.TryGetProperty("Photo", out _).Should().BeFalse();

        body.Should().NotContain(locked.ToString()).And.NotContain("passport");
    }

    [Fact]
    public async Task A_file_an_entry_names_is_in_use_and_once_force_deleted_the_entry_reads_without_it()
    {
        var type = await StoreTypeAsync("fdel", Field("Cover", "file"));
        var cover = await FileAsync(isPublic: true, "banner.png");
        var entry = await StoreEntryAsync(type, "banner", new() { ["Cover"] = cover.ToString() });

        var admin = _factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await _factory.StoredUserTokenAsync("Admin", "SuperAdmin"));

        var refused = await admin.DeleteAsync($"/api/files/{cover}", Ct);
        var refusal = await refused.Content.ReadAsStringAsync(Ct);
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict, refusal);
        using (var doc = JsonDocument.Parse(refusal))
        {
            doc.RootElement.GetProperty("total").GetInt32().Should().Be(1);
            var usages = doc.RootElement.GetProperty("usages").EnumerateArray().ToList();
            usages.Should().ContainSingle();
            usages[0].GetProperty("id").GetGuid().Should().Be(entry);
        }

        var (_, before) = await ListAsync(type);
        before.Should().ContainSingle();
        ShouldBeTheFile(DataOf(before, "banner").GetProperty("Cover"), cover, "banner.png");

        var forced = await admin.DeleteAsync($"/api/files/{cover}?force=true", Ct);
        forced.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var (_, after) = await ListAsync(type);
        after.Should().ContainSingle("the entry outlives the file it named");
        DataOf(after, "banner").TryGetProperty("Cover", out _).Should().BeFalse();

        // The authoring read still holds the id, and answers no file for it.
        var read = await admin.GetAsync($"/api/contents/{entry}", Ct);
        var body = await read.Content.ReadAsStringAsync(Ct);
        read.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var authored = JsonDocument.Parse(body);
        authored.RootElement.GetProperty("data").GetProperty("Cover").GetString().Should().Be(cover.ToString());
        authored.RootElement.TryGetProperty("files", out _).Should().BeFalse();
    }
}
