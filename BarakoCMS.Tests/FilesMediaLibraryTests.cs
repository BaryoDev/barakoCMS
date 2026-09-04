using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The API half of a media library (#113): alt text and a caption stored with a file, a where-used
/// list, a delete that refuses while the file is referenced, and a list a picker can search.
/// </summary>
/// <remarks>
/// Every refusal here is paired with the request that must still succeed, because a 404 is also
/// what a route that does not exist answers. The where-used lookup matches an entry's data by the
/// file's id and by the URL shape a client stores, since content has no typed file field: a
/// reference is whatever string the editor put in a field.
/// </remarks>
[Collection("Sequential")]
public class FilesMediaLibraryTests
{
    private readonly IntegrationTestFixture _factory;

    public FilesMediaLibraryTests(IntegrationTestFixture factory) => _factory = factory;

    private sealed record FileMeta(
        Guid Id, string FileName, string ContentType, long Size, bool IsPublic, string? PublicUrl,
        string? Alt, string? Caption);

    private sealed record PublicMeta(Guid Id, string FileName, string ContentType, long Size, string? Alt, string? Caption);

    private sealed record UsageRow(Guid Id, string ContentType, string? Title, string Status);

    private sealed record Listing<T>(List<T> Items, int Page, int PageSize, int TotalItems);

    private sealed record Refusal(string Message, int Total, List<UsageRow> Usages);

    // ---- alt text and caption --------------------------------------------------------------

    [Fact]
    public async Task Alt_and_caption_round_trip_through_patch_meta_and_list()
    {
        var admin = await AdminAsync();
        var id = await UploadAsync(admin, isPublic: false, "round-trip.png", "image/png");

        var patch = await admin.PatchAsJsonAsync($"/api/files/{id}",
            new { alt = "A red door", caption = "The door on Main Street" },
            TestContext.Current.CancellationToken);
        patch.StatusCode.Should().Be(HttpStatusCode.OK, await patch.Content.ReadAsStringAsync());

        var patched = await patch.Content.ReadFromJsonAsync<FileMeta>(TestContext.Current.CancellationToken);
        patched!.Alt.Should().Be("A red door");
        patched.Caption.Should().Be("The door on Main Street");

        var meta = await admin.GetFromJsonAsync<FileMeta>($"/api/files/{id}/meta", TestContext.Current.CancellationToken);
        meta!.Alt.Should().Be("A red door");
        meta.Caption.Should().Be("The door on Main Street");
        meta.FileName.Should().Be("round-trip.png");

        var page = await admin.GetFromJsonAsync<Listing<FileMeta>>(
            "/api/files?q=round-trip", TestContext.Current.CancellationToken);
        page!.Items.Should().ContainSingle(f => f.Id == id)
            .Which.Alt.Should().Be("A red door");
    }

    /// <summary>
    /// A field left out of the body is left alone; an empty string clears it.
    /// </summary>
    [Fact]
    public async Task Patch_leaves_an_omitted_field_alone_and_clears_an_empty_one()
    {
        var admin = await AdminAsync();
        var id = await UploadAsync(admin, isPublic: false, "partial.png", "image/png");

        await admin.PatchAsJsonAsync($"/api/files/{id}", new { alt = "Kept", caption = "Gone soon" },
            TestContext.Current.CancellationToken);
        var second = await admin.PatchAsJsonAsync($"/api/files/{id}", new { caption = "" },
            TestContext.Current.CancellationToken);
        second.StatusCode.Should().Be(HttpStatusCode.OK, await second.Content.ReadAsStringAsync());

        var meta = await admin.GetFromJsonAsync<FileMeta>($"/api/files/{id}/meta", TestContext.Current.CancellationToken);
        meta!.Alt.Should().Be("Kept", "a field the body did not name is not touched");
        meta.Caption.Should().BeNull("an empty string is how a caption is removed");
    }

    [Fact]
    public async Task Public_metadata_carries_alt_and_caption_for_a_public_file_only()
    {
        var admin = await AdminAsync();
        var anonymous = _factory.CreateClient();

        var open = await UploadAsync(admin, isPublic: true, "open.png", "image/png");
        var closed = await UploadAsync(admin, isPublic: false, "closed.png", "image/png");
        foreach (var id in new[] { open, closed })
        {
            var patch = await admin.PatchAsJsonAsync($"/api/files/{id}", new { alt = "Described", caption = "Captioned" },
                TestContext.Current.CancellationToken);
            patch.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var served = await anonymous.GetAsync($"/api/public/files/{open}/meta", TestContext.Current.CancellationToken);
        served.StatusCode.Should().Be(HttpStatusCode.OK, await served.Content.ReadAsStringAsync());
        var meta = await served.Content.ReadFromJsonAsync<PublicMeta>(TestContext.Current.CancellationToken);
        meta!.Alt.Should().Be("Described");
        meta.Caption.Should().Be("Captioned");
        meta.ContentType.Should().Be("image/png");

        var refused = await anonymous.GetAsync($"/api/public/files/{closed}/meta", TestContext.Current.CancellationToken);
        refused.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a private file's metadata is as private as its bytes, and indistinguishable from missing");

        var missing = await anonymous.GetAsync($"/api/public/files/{Guid.NewGuid()}/meta", TestContext.Current.CancellationToken);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// The validator is the only thing between a 500-character limit and an unbounded string in
    /// the document, so its wiring is proved here rather than assumed from the neighbour.
    /// </summary>
    [Fact]
    public async Task Patch_refuses_alt_text_over_the_limit_and_changes_nothing()
    {
        var admin = await AdminAsync();
        var id = await UploadAsync(admin, isPublic: false, "limits.png", "image/png");

        var first = await admin.PatchAsJsonAsync($"/api/files/{id}", new { alt = "Kept" }, TestContext.Current.CancellationToken);
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var tooLong = await admin.PatchAsJsonAsync($"/api/files/{id}", new { alt = new string('a', 501) },
            TestContext.Current.CancellationToken);
        tooLong.StatusCode.Should().Be(HttpStatusCode.BadRequest, await tooLong.Content.ReadAsStringAsync());

        var meta = await admin.GetFromJsonAsync<FileMeta>($"/api/files/{id}/meta", TestContext.Current.CancellationToken);
        meta!.Alt.Should().Be("Kept", "a refused update writes nothing");
    }

    // ---- where used -------------------------------------------------------------------------

    [Fact]
    public async Task Usage_lists_the_entries_that_reference_the_file_by_id_or_url_and_not_the_rest()
    {
        var admin = await AdminAsync();
        var id = await UploadAsync(admin, isPublic: true, "used.png", "image/png");
        var type = await SeedTypeAsync();

        var byId = await StoreEntryAsync(type, new() { ["Title"] = "By id", ["Image"] = id.ToString() });
        var byUrl = await StoreEntryAsync(type, new()
        {
            ["Title"] = "By url",
            ["Hero"] = $"https://cms.example.com/api/public/files/{id}?w=640",
        }, ContentStatus.Draft);
        var unrelated = await StoreEntryAsync(type, new() { ["Title"] = "Other", ["Image"] = Guid.NewGuid().ToString() });

        var response = await admin.GetAsync($"/api/files/{id}/usage", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var page = await response.Content.ReadFromJsonAsync<Listing<UsageRow>>(TestContext.Current.CancellationToken);

        page!.Items.Should().HaveCount(2, "one entry holds the id and one holds the URL");
        page.TotalItems.Should().Be(2);
        page.Items.Select(u => u.Id).Should().BeEquivalentTo([byId, byUrl]);
        page.Items.Should().NotContain(u => u.Id == unrelated);

        var row = page.Items.Single(u => u.Id == byUrl);
        row.ContentType.Should().Be(type);
        row.Title.Should().Be("By url");
        row.Status.Should().Be("Draft");
    }

    [Fact]
    public async Task Usage_of_an_unreferenced_file_is_an_empty_page_and_of_a_missing_file_a_404()
    {
        var admin = await AdminAsync();
        var id = await UploadAsync(admin, isPublic: false, "lonely.png", "image/png");

        var page = await admin.GetFromJsonAsync<Listing<UsageRow>>($"/api/files/{id}/usage", TestContext.Current.CancellationToken);
        page!.Items.Should().BeEmpty();
        page.TotalItems.Should().Be(0);

        var missing = await admin.GetAsync($"/api/files/{Guid.NewGuid()}/usage", TestContext.Current.CancellationToken);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// The usage rows and the 409 body go through the same two checks as GET /api/contents: the
    /// entry is listed whatever the caller may read of it, because the count is what stops a
    /// delete, but its title is only there when the caller holds read on the type and the
    /// sensitivity scrub leaves it.
    /// </summary>
    [Fact]
    public async Task Usage_and_the_refusal_hide_the_title_of_an_entry_the_caller_may_not_read()
    {
        var admin = await AdminAsync();
        var id = await UploadAsync(admin, isPublic: true, "minutes.png", "image/png");
        var type = await SeedTypeAsync();
        var otherType = await SeedTypeAsync();
        var editor = await MediaEditorAsync(readableType: type);

        var open = await StoreEntryAsync(type, new() { ["Title"] = "Open page", ["Image"] = id.ToString() });
        var secret = await StoreEntryAsync(type, new() { ["Title"] = "Board minutes", ["Image"] = id.ToString() },
            sensitivity: SensitivityLevel.Sensitive);
        var hidden = await StoreEntryAsync(type, new() { ["Title"] = "Payroll", ["Image"] = id.ToString() },
            sensitivity: SensitivityLevel.Hidden);
        var unreadable = await StoreEntryAsync(otherType, new() { ["Title"] = "Invoice for Acme", ["Image"] = id.ToString() });

        var page = await editor.GetFromJsonAsync<Listing<UsageRow>>($"/api/files/{id}/usage", TestContext.Current.CancellationToken);
        page!.Items.Should().HaveCount(4, "every entry counts as a usage, whatever the caller may read of it");
        page.Items.Single(u => u.Id == open).Title.Should().Be("Open page");
        page.Items.Single(u => u.Id == secret).Title.Should().BeNull(
            "a Sensitive entry's fields are cleared for a caller outside its roles, as on GET /api/contents");
        page.Items.Single(u => u.Id == hidden).Title.Should().BeNull();
        page.Items.Single(u => u.Id == hidden).ContentType.Should().Be("HIDDEN", "a Hidden entry names its type to nobody but SuperAdmin");
        page.Items.Single(u => u.Id == unreadable).Title.Should().BeNull(
            "no read permission on the type means no field of the entry, as on GET /api/contents");

        var refused = await editor.DeleteAsync($"/api/files/{id}", TestContext.Current.CancellationToken);
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict, await refused.Content.ReadAsStringAsync());
        var refusal = await refused.Content.ReadFromJsonAsync<Refusal>(TestContext.Current.CancellationToken);
        refusal!.Total.Should().Be(4);
        refusal.Usages.Single(u => u.Id == open).Title.Should().Be("Open page");
        refusal.Usages.Single(u => u.Id == secret).Title.Should().BeNull();
        refusal.Usages.Single(u => u.Id == unreadable).Title.Should().BeNull();

        var asAdmin = await admin.GetFromJsonAsync<Listing<UsageRow>>($"/api/files/{id}/usage", TestContext.Current.CancellationToken);
        asAdmin!.Items.Should().HaveCount(4);
        asAdmin.Items.Single(u => u.Id == secret).Title.Should().Be("Board minutes", "SuperAdmin sees everything");
        asAdmin.Items.Single(u => u.Id == hidden).Title.Should().Be("Payroll");
        asAdmin.Items.Single(u => u.Id == unreadable).Title.Should().Be("Invoice for Acme");
    }

    // ---- delete -----------------------------------------------------------------------------

    [Fact]
    public async Task Delete_is_refused_with_409_while_the_file_is_used_and_names_the_usages()
    {
        var admin = await AdminAsync();
        var id = await UploadAsync(admin, isPublic: true, "in-use.png", "image/png");
        var type = await SeedTypeAsync();
        var entry = await StoreEntryAsync(type, new() { ["Title"] = "Page with image", ["Image"] = id.ToString() });

        var response = await admin.DeleteAsync($"/api/files/{id}", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync());
        var refusal = await response.Content.ReadFromJsonAsync<Refusal>(TestContext.Current.CancellationToken);
        refusal!.Total.Should().Be(1);
        refusal.Usages.Should().ContainSingle().Which.Id.Should().Be(entry);
        refusal.Usages[0].Title.Should().Be("Page with image");

        var still = await admin.GetAsync($"/api/files/{id}/meta", TestContext.Current.CancellationToken);
        still.StatusCode.Should().Be(HttpStatusCode.OK, "a refused delete removes nothing");
        var bytes = await _factory.CreateClient().GetAsync($"/api/public/files/{id}", TestContext.Current.CancellationToken);
        bytes.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Delete_with_force_removes_a_used_file()
    {
        var admin = await AdminAsync();
        var id = await UploadAsync(admin, isPublic: true, "forced.png", "image/png");
        var type = await SeedTypeAsync();
        await StoreEntryAsync(type, new() { ["Title"] = "Still points here", ["Image"] = id.ToString() });

        var response = await admin.DeleteAsync($"/api/files/{id}?force=true", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());
        var meta = await admin.GetAsync($"/api/files/{id}/meta", TestContext.Current.CancellationToken);
        meta.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var bytes = await _factory.CreateClient().GetAsync($"/api/public/files/{id}", TestContext.Current.CancellationToken);
        bytes.StatusCode.Should().Be(HttpStatusCode.NotFound, "the bytes go with the record");
    }

    [Fact]
    public async Task Delete_of_an_unused_file_needs_no_force()
    {
        var admin = await AdminAsync();
        var id = await UploadAsync(admin, isPublic: false, "unused.png", "image/png");

        var response = await admin.DeleteAsync($"/api/files/{id}", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());
        var again = await admin.DeleteAsync($"/api/files/{id}", TestContext.Current.CancellationToken);
        again.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- list -------------------------------------------------------------------------------

    [Fact]
    public async Task List_filters_by_name_substring_and_content_type_prefix()
    {
        var admin = await AdminAsync();
        var stamp = Guid.NewGuid().ToString("N")[..8];
        var image = await UploadAsync(admin, isPublic: false, $"hero-banner-{stamp}.png", "image/png");
        var pdf = await UploadAsync(admin, isPublic: false, $"invoice-{stamp}.pdf", "application/pdf");

        var byName = await admin.GetFromJsonAsync<Listing<FileMeta>>(
            $"/api/files?q=BANNER-{stamp}", TestContext.Current.CancellationToken);
        byName!.Items.Should().ContainSingle(f => f.Id == image, "the name filter is a case-insensitive substring");
        byName.Items.Should().NotContain(f => f.Id == pdf);

        var images = await admin.GetFromJsonAsync<Listing<FileMeta>>(
            $"/api/files?q={stamp}&contentType=image/", TestContext.Current.CancellationToken);
        images!.Items.Should().ContainSingle(f => f.Id == image, "image/ is a prefix a picker filters on");
        images.Items.Should().NotContain(f => f.Id == pdf);

        var both = await admin.GetFromJsonAsync<Listing<FileMeta>>(
            $"/api/files?q={stamp}&pageSize=1", TestContext.Current.CancellationToken);
        both!.Items.Should().HaveCount(1, "the page size is honoured");
        both.TotalItems.Should().Be(2);
    }

    // ---- gates ------------------------------------------------------------------------------

    public static TheoryData<string, string> GatedRoutes() => new()
    {
        { "GET", "/api/files" },
        { "GET", $"/api/files/{Guid.NewGuid()}/meta" },
        { "PATCH", $"/api/files/{Guid.NewGuid()}" },
        { "GET", $"/api/files/{Guid.NewGuid()}/usage" },
        { "DELETE", $"/api/files/{Guid.NewGuid()}" },
    };

    [Theory]
    [MemberData(nameof(GatedRoutes))]
    public async Task A_new_route_is_refused_anonymously_and_to_a_user_role_and_still_answers_an_admin(string verb, string path)
    {
        var anonymous = await _factory.CreateClient().SendAsync(Probe(verb, path), TestContext.Current.CancellationToken);
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var user = await UserAsync();
        var refused = await user.SendAsync(Probe(verb, path), TestContext.Current.CancellationToken);
        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "self-registration grants the User role, and it must not reach the media library");

        var admin = await AdminAsync();
        var served = await admin.SendAsync(Probe(verb, path), TestContext.Current.CancellationToken);
        served.StatusCode.Should().BeOneOf([HttpStatusCode.OK, HttpStatusCode.NotFound, HttpStatusCode.BadRequest]);
        served.StatusCode.Should().NotBe(HttpStatusCode.Forbidden, "the gate lets an admin through");
    }

    // ---- tenancy ----------------------------------------------------------------------------

    [Fact]
    public async Task A_file_in_another_tenant_is_invisible_to_every_route()
    {
        var admin = await AdminAsync();
        var id = await UploadAsync(admin, isPublic: true, "tenant-a.png", "image/png");

        var other = await MemberOfAnotherTenantAsync();

        var meta = await other.GetAsync($"/api/files/{id}/meta", TestContext.Current.CancellationToken);
        var usage = await other.GetAsync($"/api/files/{id}/usage", TestContext.Current.CancellationToken);
        var patch = await other.PatchAsJsonAsync($"/api/files/{id}", new { alt = "x" }, TestContext.Current.CancellationToken);
        var remove = await other.DeleteAsync($"/api/files/{id}?force=true", TestContext.Current.CancellationToken);
        var list = await other.GetFromJsonAsync<Listing<FileMeta>>("/api/files?q=tenant-a", TestContext.Current.CancellationToken);

        meta.StatusCode.Should().Be(HttpStatusCode.NotFound);
        usage.StatusCode.Should().Be(HttpStatusCode.NotFound);
        patch.StatusCode.Should().Be(HttpStatusCode.NotFound);
        remove.StatusCode.Should().Be(HttpStatusCode.NotFound);
        list!.Items.Should().NotContain(f => f.Id == id, "a list that forgot its tenant filter returns everybody's rows");

        var own = await admin.GetAsync($"/api/files/{id}/meta", TestContext.Current.CancellationToken);
        own.StatusCode.Should().Be(HttpStatusCode.OK, "isolation that also blocks the owning tenant is an outage");
    }

    // ---- helpers ----------------------------------------------------------------------------

    private static HttpRequestMessage Probe(string verb, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(verb), path);
        if (verb is "PATCH" or "POST" or "PUT")
        {
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        }
        return request;
    }

    private async Task<HttpClient> AdminAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var role = await s.Query<Role>().FirstOrDefaultAsync(r => r.Name == "SuperAdmin")
                   ?? new Role { Id = barakoCMS.Data.DataSeeder.SuperAdminRoleId, Name = "SuperAdmin", Permissions = new() };
        s.Store(role);
        var userId = Guid.NewGuid();
        s.Store(new User { Id = userId, Username = $"media-{userId}", Email = $"media-{userId}@example.com", RoleIds = new() { role.Id } });
        await s.SaveChangesAsync();

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(["SuperAdmin"], userId.ToString()));
        return client;
    }

    private async Task<HttpClient> UserAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var role = await s.Query<Role>().FirstOrDefaultAsync(r => r.Name == "User")
                   ?? new Role { Id = Guid.NewGuid(), Name = "User", Permissions = new() };
        s.Store(role);
        var userId = Guid.NewGuid();
        s.Store(new User { Id = userId, Username = $"plain-{userId}", Email = $"plain-{userId}@example.com", RoleIds = new() { role.Id } });
        await s.SaveChangesAsync();

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(["User"], userId.ToString()));
        return client;
    }

    /// <summary>
    /// A runtime role holding upload_files and read on one content type: the media editor a client
    /// would create.
    /// </summary>
    private async Task<HttpClient> MediaEditorAsync(string readableType)
    {
        var name = $"Media Editor {Guid.NewGuid():N}";
        var userId = Guid.NewGuid();
        using var scope = _factory.Services.CreateScope();
        var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = name,
            SystemCapabilities = [BarakoCMS.Files.FileCapabilities.UploadFiles],
            Permissions =
            [
                new ContentTypePermission { ContentTypeSlug = readableType, Read = new PermissionRule { Enabled = true } },
            ],
        };
        s.Store(role);
        s.Store(new User { Id = userId, Username = $"editor-{userId}", Email = $"editor-{userId}@example.com", RoleIds = [role.Id] });
        await s.SaveChangesAsync();

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken([name], userId.ToString()));
        return client;
    }

    private async Task<HttpClient> MemberOfAnotherTenantAsync()
    {
        var slug = $"media-{Guid.NewGuid():N}"[..14].ToLowerInvariant();
        var userId = Guid.NewGuid();

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug, IsActive = true });
            session.Store(new User
            {
                Id = userId,
                Username = $"xt-{Guid.NewGuid():n}"[..14],
                Email = $"xt-{Guid.NewGuid():n}@example.com",
                RoleIds = [SystemRoles.SuperAdminRoleId],
            });
            session.Store(new Membership
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                TenantSlug = slug,
                Status = MembershipStatus.Active,
                RoleIds = [SystemRoles.SuperAdminRoleId],
            });
            await session.SaveChangesAsync();
        }

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(
                roles: ["SuperAdmin"],
                userId: userId.ToString(),
                additionalClaims: new Dictionary<string, string> { ["tenant"] = slug }));
        client.DefaultRequestHeaders.Add("X-Tenant", slug);
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, $"10.113.0.{Random.Shared.Next(1, 250)}");
        return client;
    }

    private async Task<Guid> UploadAsync(HttpClient client, bool isPublic, string name, string contentType)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent([1, 2, 3, 4, 5]);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", name);
        form.Add(new StringContent(isPublic ? "true" : "false"), "isPublic");

        var res = await client.PostAsync("/api/files", form, TestContext.Current.CancellationToken);
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        var body = await res.Content.ReadFromJsonAsync<FileMeta>(TestContext.Current.CancellationToken);
        return body!.Id;
    }

    /// <summary>A type with a string field and a url field, which is all a file reference is today.</summary>
    private async Task<string> SeedTypeAsync()
    {
        var name = $"media_page_{Guid.NewGuid():N}"[..24];
        using var scope = _factory.Services.CreateScope();
        var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        s.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(), Name = name, DisplayName = name,
            Fields =
            [
                new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" },
                new FieldDefinition { Name = "Image", DisplayName = "Image", Type = "string" },
                new FieldDefinition { Name = "Hero", DisplayName = "Hero", Type = "url" },
            ],
        });
        await s.SaveChangesAsync();
        return name;
    }

    private async Task<Guid> StoreEntryAsync(string type, Dictionary<string, object> data,
        ContentStatus status = ContentStatus.Published, SensitivityLevel sensitivity = SensitivityLevel.Public)
    {
        using var scope = _factory.Services.CreateScope();
        var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var id = Guid.NewGuid();
        s.Store(new Content
        {
            Id = id, ContentType = type, Status = status,
            Sensitivity = sensitivity, Data = data,
        });
        await s.SaveChangesAsync();
        return id;
    }
}
