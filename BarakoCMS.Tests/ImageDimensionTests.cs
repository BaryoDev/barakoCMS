using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using BarakoCMS.Files;
using barakoCMS.Core.Interfaces;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// An image's width and height are read on upload and returned from the metadata routes, and a
/// variant reports its own (#667).
/// </summary>
[Collection("Integration1")]
public class ImageDimensionTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;

    public ImageDimensionTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<string> TokenAsync() => _factory.StoredUserTokenAsync("SuperAdmin");

    private async Task<JsonElement> UploadAsync(string token, byte[] bytes, string contentType, string name, bool isPublic = true)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", name);
        form.Add(new StringContent(isPublic ? "true" : "false"), "isPublic");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/files") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await _client.SendAsync(request, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }

    private async Task<JsonElement> GetAsync(string url, string? token = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        var response = await _client.SendAsync(request, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(expected, body);
        if (expected != HttpStatusCode.OK)
        {
            return default;
        }

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }

    private static (int? Width, int? Height) SizeOf(JsonElement json) => (
        json.GetProperty("width").ValueKind == JsonValueKind.Null ? null : json.GetProperty("width").GetInt32(),
        json.GetProperty("height").ValueKind == JsonValueKind.Null ? null : json.GetProperty("height").GetInt32());

    private async Task<StoredFile> LoadAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<StoredFile>(id, Ct))!;
    }

    [Fact]
    public async Task An_uploaded_png_reports_its_size_on_every_route()
    {
        var token = await TokenAsync();

        var uploaded = await UploadAsync(token, FileSamples.Noise(1200, 800), "image/png", "wide.png");
        var id = uploaded.GetProperty("id").GetGuid();

        SizeOf(uploaded).Should().Be((1200, 800));
        SizeOf(await GetAsync($"/api/files/{id}/meta", token)).Should().Be((1200, 800));
        SizeOf(await GetAsync($"/api/public/files/{id}/meta")).Should().Be((1200, 800));
        var stored = await LoadAsync(id);
        (stored.Width, stored.Height).Should().Be((1200, 800));
    }

    [Fact]
    public async Task A_jpeg_tagged_as_turned_a_quarter_reports_the_size_it_is_shown_at()
    {
        var token = await TokenAsync();
        var jpeg = FileSamples.WithOrientation(FileSamples.Noise(400, 300, SKEncodedImageFormat.Jpeg), 6);

        var uploaded = await UploadAsync(token, jpeg, "image/jpeg", "sideways.jpg");

        SizeOf(uploaded).Should().Be((300, 400), "stored 400x300 and shown turned");
    }

    [Fact]
    public async Task A_pdf_has_no_size()
    {
        var uploaded = await UploadAsync(await TokenAsync(), FileSamples.Pdf(), "application/pdf", "doc.pdf");

        SizeOf(uploaded).Should().Be(((int?)null, (int?)null));
    }

    [Fact]
    public async Task A_file_saved_through_the_file_store_records_its_size()
    {
        using var scope = _factory.Services.CreateScope();
        using var content = new MemoryStream(FileSamples.Noise(320, 200, SKEncodedImageFormat.Webp));

        var saved = await scope.ServiceProvider.GetRequiredService<IFileStore>().SaveAsync(new FileToStore
        {
            Content = content,
            FileName = "inline.webp",
            ContentType = "image/webp",
            IsPublic = true,
            Owner = Guid.NewGuid(),
        }, Ct);

        saved.File.Should().NotBeNull(saved.Refused);
        var stored = await LoadAsync(saved.File!.Id);
        (stored.Width, stored.Height).Should().Be((320, 200));
    }

    [Fact]
    public async Task The_public_metadata_at_a_width_describes_the_variant_served_there()
    {
        var id = (await UploadAsync(await TokenAsync(), FileSamples.Noise(1200, 800), "image/png", "wide.png"))
            .GetProperty("id").GetGuid();

        var atRung = await GetAsync($"/api/public/files/{id}/meta?w=600");

        SizeOf(atRung).Should().Be((640, 427), "600 is served from the 640 rung, and 800 * 640 / 1200 rounds to 427");
        var variant = await LoadAsync(ImageVariants.VariantId(id, 640));
        (variant.Width, variant.Height).Should().Be((640, 427), "the variant recorded its own size when it was made");
        atRung.GetProperty("size").GetInt64().Should().Be(variant.Size);

        SizeOf(await GetAsync($"/api/public/files/{id}/meta?w=1500")).Should().Be((1200, 800),
            "the original is narrower than the rung, so it is what is served");
        SizeOf(await GetAsync($"/api/public/files/{id}/meta")).Should().Be((1200, 800));
        SizeOf(await GetAsync($"/api/public/files/{id}/meta?w=5000")).Should().Be((1200, 800),
            "a width the download refuses is ignored here, as every width was before this route read one");
    }

    [Fact]
    public async Task A_private_file_has_no_public_metadata_at_any_width()
    {
        var token = await TokenAsync();
        var id = (await UploadAsync(token, FileSamples.Noise(1200, 800), "image/png", "private.png", isPublic: false))
            .GetProperty("id").GetGuid();

        SizeOf(await GetAsync($"/api/files/{id}/meta", token)).Should().Be((1200, 800), "the positive control");
        await GetAsync($"/api/public/files/{id}/meta?w=640", expected: HttpStatusCode.NotFound);
        await GetAsync($"/api/public/files/{id}/meta", expected: HttpStatusCode.NotFound);
    }
}
