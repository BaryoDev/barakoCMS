using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// An upload is stored only when its declared type is exactly an allowed one and its bytes are that
/// type. The declared type used to be matched as a prefix and never compared with the bytes.
/// </summary>
[Collection("Sequential")]
public class UploadTypeTests
{
    private readonly IntegrationTestFixture _factory;

    public UploadTypeTests(IntegrationTestFixture factory) => _factory = factory;

    private sealed record Uploaded(Guid Id, string FileName, string ContentType, long Size, bool IsPublic, string? PublicUrl);

    public static TheoryData<string, string> AllowedTypes() => new()
    {
        { "image/png", "a.png" },
        { "image/jpeg", "a.jpg" },
        { "image/gif", "a.gif" },
        { "image/webp", "a.webp" },
        { "image/avif", "a.avif" },
        { "application/pdf", "a.pdf" },
    };

    [Theory]
    [MemberData(nameof(AllowedTypes))]
    public async Task A_real_file_of_each_allowed_type_is_stored_and_served_back_unchanged(string contentType, string name)
    {
        var (client, _) = await AdminAsync();
        var bytes = FileSamples.For(contentType);

        var response = await UploadAsync(client, contentType, name, bytes);

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var body = await response.Content.ReadFromJsonAsync<Uploaded>(TestContext.Current.CancellationToken);
        body!.ContentType.Should().Be(contentType);

        var served = await client.GetAsync($"/api/files/{body.Id}", TestContext.Current.CancellationToken);
        served.StatusCode.Should().Be(HttpStatusCode.OK);
        (await served.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Should().Equal(bytes);
    }

    [Fact]
    public async Task Html_declared_as_a_png_is_refused_and_nothing_is_stored()
    {
        var (client, _) = await AdminAsync();
        var name = $"page-{Guid.NewGuid():N}.png";
        var html = Encoding.UTF8.GetBytes("<!doctype html><html><body><script>document.title='x'</script></body></html>");

        var response = await UploadAsync(client, "image/png", name, html);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "the declared type is the client's word, and the bytes say it is not a PNG");
        (await StoredNamedAsync(name)).Should().Be(0);
    }

    [Theory]
    [InlineData("image/pngx", "a.png")]
    [InlineData("image/png-evil", "a.png")]
    [InlineData("application/pdfx", "a.pdf")]
    [InlineData("image/jpeg2000", "a.jpg")]
    public async Task A_type_that_only_starts_with_an_allowed_one_is_refused(string declared, string name)
    {
        var (client, _) = await AdminAsync();
        var bytes = declared.StartsWith("application/pdf") ? FileSamples.Pdf()
            : declared.StartsWith("image/jpeg") ? FileSamples.Image("image/jpeg")
            : FileSamples.Image("image/png");
        var unique = $"{Guid.NewGuid():N}-{name}";

        var response = await UploadAsync(client, declared, unique, bytes);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            $"{declared} is not on the list, whatever it starts with, and it would be stored and served as sent");
        (await StoredNamedAsync(unique)).Should().Be(0);
    }

    [Fact]
    public async Task A_declared_type_is_stored_bare_and_lower_case()
    {
        var (client, _) = await AdminAsync();

        var response = await UploadAsync(client, "IMAGE/PNG", "upper.png", FileSamples.Image("image/png"));

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var body = await response.Content.ReadFromJsonAsync<Uploaded>(TestContext.Current.CancellationToken);
        body!.ContentType.Should().Be("image/png", "the stored type is the one on the list, not the client's spelling");
    }

    [Fact]
    public async Task A_private_download_is_an_attachment_the_browser_may_not_sniff()
    {
        var (client, _) = await AdminAsync();
        var created = await UploadAsync(client, "application/pdf", "report.pdf", FileSamples.Pdf());
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await created.Content.ReadFromJsonAsync<Uploaded>(TestContext.Current.CancellationToken))!.Id;

        var served = await client.GetAsync($"/api/files/{id}", TestContext.Current.CancellationToken);

        served.StatusCode.Should().Be(HttpStatusCode.OK);
        served.Headers.GetValues("X-Content-Type-Options").Should().ContainSingle().Which.Should().Be("nosniff");
        served.Headers.GetValues("Content-Security-Policy").Should().ContainSingle()
            .Which.Should().Contain("sandbox");
        served.Content.Headers.ContentDisposition?.DispositionType.Should().Be("attachment");
    }

    [Fact]
    public async Task A_type_with_parameters_is_stored_bare_and_lower_case()
    {
        var (client, _) = await AdminAsync();

        var response = await UploadAsync(client, "IMAGE/PNG; x=y", "params.png", FileSamples.Image("image/png"));

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var body = await response.Content.ReadFromJsonAsync<Uploaded>(TestContext.Current.CancellationToken);
        body!.ContentType.Should().Be("image/png");
    }

    [Fact]
    public async Task The_storage_key_takes_its_extension_from_the_checked_type_not_the_file_name()
    {
        var (client, _) = await AdminAsync();

        var response = await UploadAsync(client, "image/png", "picture.html", FileSamples.Image("image/png"));

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var id = (await response.Content.ReadFromJsonAsync<Uploaded>(TestContext.Current.CancellationToken))!.Id;
        var stored = await LoadAsync(id);
        stored.StorageKey.Should().EndWith(".png",
            "an object store serves by the key's extension, and the client's name is not evidence of the type");
        stored.FileName.Should().Be("picture.html", "the name the person gave is still what they see");
    }

    /// <summary>
    /// A PNG signature followed by HTML is a PNG as far as the check can tell, so it is stored. What
    /// keeps it harmless is how it is served, on both download routes.
    /// </summary>
    [Fact]
    public async Task A_png_carrying_html_after_its_signature_is_served_as_an_attachment_that_cannot_run()
    {
        var (client, _) = await AdminAsync();
        var polyglot = FileSamples.Png(Encoding.UTF8.GetBytes("<html><script>document.title='x'</script></html>"));

        var created = await UploadAsync(client, "image/png", "poly.png", polyglot, isPublic: true);
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var id = (await created.Content.ReadFromJsonAsync<Uploaded>(TestContext.Current.CancellationToken))!.Id;

        var responses = new[]
        {
            await client.GetAsync($"/api/files/{id}", TestContext.Current.CancellationToken),
            await _factory.CreateClient().GetAsync($"/api/public/files/{id}", TestContext.Current.CancellationToken),
        };

        responses.Should().HaveCount(2);
        foreach (var served in responses)
        {
            served.StatusCode.Should().Be(HttpStatusCode.OK);
            served.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
            AssertCannotRun(served);
        }
    }

    /// <summary>
    /// A row stored before uploads were checked can carry any type its client sent. Redirecting to
    /// the object store would serve it from there with none of this API's headers, so such a row is
    /// streamed through the API instead, as a download of unknown type.
    /// </summary>
    [Fact]
    public async Task An_old_public_row_with_a_type_that_is_not_bare_is_streamed_not_redirected()
    {
        var (client, adminId) = await AdminAsync();
        var bytes = Encoding.UTF8.GetBytes("<html><script>document.title='x'</script></html>");
        var id = await SeedAsync("image/png, text/html", bytes, adminId, "https://store.invalid/old.html");

        var responses = new[]
        {
            await NoRedirects(client).GetAsync($"/api/files/{id}", TestContext.Current.CancellationToken),
            await NoRedirects(null).GetAsync($"/api/public/files/{id}", TestContext.Current.CancellationToken),
        };

        responses.Should().HaveCount(2);
        foreach (var served in responses)
        {
            served.StatusCode.Should().Be(HttpStatusCode.OK, "a redirect would hand the stored type to the store to serve");
            served.Content.Headers.ContentType!.MediaType.Should().Be("application/octet-stream");
            (await served.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Should().Equal(bytes);
            AssertCannotRun(served);
        }
    }

    [Fact]
    public async Task A_public_row_with_an_allowed_type_still_redirects_to_the_store()
    {
        var (client, adminId) = await AdminAsync();
        var id = await SeedAsync("image/png", FileSamples.Image("image/png"), adminId, "https://store.invalid/new.png");

        var owner = await NoRedirects(client).GetAsync($"/api/files/{id}", TestContext.Current.CancellationToken);
        var anonymous = await NoRedirects(null).GetAsync($"/api/public/files/{id}", TestContext.Current.CancellationToken);

        owner.StatusCode.Should().Be(HttpStatusCode.Redirect);
        owner.Headers.Location!.ToString().Should().Be("https://store.invalid/new.png");
        anonymous.StatusCode.Should().Be(HttpStatusCode.Redirect);
    }

    private static void AssertCannotRun(HttpResponseMessage served)
    {
        served.Content.Headers.ContentDisposition?.DispositionType.Should().Be("attachment");
        served.Headers.GetValues("X-Content-Type-Options").Should().ContainSingle().Which.Should().Be("nosniff");
        served.Headers.GetValues("Content-Security-Policy").Should().ContainSingle().Which.Should().Contain("sandbox");
    }

    private HttpClient NoRedirects(HttpClient? authenticated)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (authenticated?.DefaultRequestHeaders.Authorization is { } auth)
        {
            client.DefaultRequestHeaders.Authorization = auth;
        }
        return client;
    }

    private async Task<Guid> SeedAsync(string contentType, byte[] bytes, Guid uploadedBy, string publicUrl)
    {
        using var scope = _factory.Services.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<BarakoCMS.Files.IFileStorage>();
        var id = Guid.NewGuid();
        var stored = await storage.PutAsync(
            new MemoryStream(bytes), $"{id:N}.html", contentType, true, TestContext.Current.CancellationToken);

        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new BarakoCMS.Files.StoredFile
        {
            Id = id,
            FileName = "old.html",
            ContentType = contentType,
            Size = bytes.Length,
            Provider = storage.Provider,
            StorageKey = stored.Key,
            IsPublic = true,
            PublicUrl = publicUrl,
            UploadedBy = uploadedBy,
            CreatedAt = DateTime.UtcNow,
        });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return id;
    }

    private async Task<BarakoCMS.Files.StoredFile> LoadAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var file = await scope.ServiceProvider.GetRequiredService<IQuerySession>()
            .LoadAsync<BarakoCMS.Files.StoredFile>(id, TestContext.Current.CancellationToken);
        return file!;
    }

    private static async Task<HttpResponseMessage> UploadAsync(
        HttpClient client, string contentType, string name, byte[] bytes, bool isPublic = false)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.TryAddWithoutValidation("Content-Type", contentType);
        form.Add(file, "file", name);
        form.Add(new StringContent(isPublic ? "true" : "false"), "isPublic");
        return await client.PostAsync("/api/files", form, TestContext.Current.CancellationToken);
    }

    private async Task<int> StoredNamedAsync(string name)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IQuerySession>()
            .Query<BarakoCMS.Files.StoredFile>()
            .CountAsync(f => f.FileName == name, TestContext.Current.CancellationToken);
    }

    private async Task<(HttpClient Client, Guid UserId)> AdminAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var role = await s.Query<Role>().FirstOrDefaultAsync(r => r.Name == "SuperAdmin")
                   ?? new Role { Id = barakoCMS.Data.DataSeeder.SuperAdminRoleId, Name = "SuperAdmin", Permissions = new() };
        s.Store(role);
        var userId = Guid.NewGuid();
        s.Store(new User { Id = userId, Username = $"types-{userId}", Email = $"types-{userId}@example.com", RoleIds = new() { role.Id } });
        await s.SaveChangesAsync();

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(["SuperAdmin"], userId.ToString()));
        return (client, userId);
    }
}
