using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BarakoCMS.Files;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using barakoCMS.Models;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>Counts resizes so a test can tell a cache hit from a second resize.</summary>
internal sealed class CountingResizer : IImageResizer
{
    private readonly IImageResizer _inner;

    public CountingResizer(IImageResizer inner) => _inner = inner;

    public int Resizes { get; private set; }

    public bool CanResize(string contentType) => _inner.CanResize(contentType);

    public Task<byte[]?> ResizeAsync(byte[] source, int width, CancellationToken ct = default)
    {
        Resizes++;
        return _inner.ResizeAsync(source, width, ct);
    }
}

/// <summary>
/// On-request image variants: <c>?w=</c> on either download route.
/// </summary>
/// <remarks>
/// The security-relevant half is that a variant inherits its original's readability and cannot be
/// reached any other way. Two things carry that: the access check runs on the original before a
/// resize is even considered, and a derived record is not addressable by its own id. Both are
/// tested, and both refusals are paired with the request that must still succeed, because "it
/// returned 404" is also what a completely broken download route returns.
/// </remarks>
[Collection("Sequential")]
public class ImageVariantTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;

    public ImageVariantTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    /// <summary>
    /// A real PNG, because the whole feature is a decoder and an encoder. Noise rather than a flat
    /// fill so the encoded bytes are not so small that a resize and an original could coincide.
    /// </summary>
    private static byte[] Png(int width, int height)
    {
        using var image = new Image<Rgba32>(width, height);
        var random = new Random(width * 31 + height);

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    row[x] = new Rgba32((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), 255);
                }
            }
        });

        using var output = new MemoryStream();
        image.Save(output, new PngEncoder());
        return output.ToArray();
    }

    private static int WidthOf(byte[] bytes) => Image.Identify(bytes).Width;

    private async Task<string> AdminTokenAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var role = await session.Query<Role>().FirstOrDefaultAsync(r => r.Name == "SuperAdmin")
                   ?? new Role { Id = barakoCMS.Data.DataSeeder.SuperAdminRoleId, Name = "SuperAdmin", Permissions = new() };
        session.Store(role);

        var userId = Guid.NewGuid();
        session.Store(new User
        {
            Id = userId,
            Username = $"admin-{userId}",
            Email = $"admin-{userId}@example.com",
            RoleIds = new() { role.Id },
        });
        await session.SaveChangesAsync();

        return _factory.CreateToken(new[] { "SuperAdmin" }, userId.ToString());
    }

    private async Task<Guid> UploadAsync(
        string token, bool isPublic, byte[] bytes, string contentType = "image/png", string name = "pic.png")
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", name);
        form.Add(new StringContent(isPublic ? "true" : "false"), "isPublic");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/files") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.Created,
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var body = await response.Content.ReadFromJsonAsync<UploadResponse>(TestContext.Current.CancellationToken);
        return body!.Id;
    }

    private async Task<HttpResponseMessage> GetWithAuthAsync(HttpClient client, string token, string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task<IReadOnlyList<StoredFile>> VariantsOfAsync(Guid parent)
    {
        using var scope = _factory.Services.CreateScope();
        var found = await scope.ServiceProvider.GetRequiredService<IQuerySession>()
            .Query<StoredFile>()
            .Where(f => f.ParentFileId == parent)
            .ToListAsync(TestContext.Current.CancellationToken);

        return found;
    }

    private sealed record UploadResponse(Guid Id, string FileName, string ContentType, long Size, bool IsPublic, string? PublicUrl);

    /// <summary>
    /// A host whose resizer counts, so a test can assert on whether one happened rather than only on
    /// what came back. Do not dispose the derived factory; the fixture owns the lifetime.
    /// </summary>
    private (HttpClient Client, CountingResizer Counter) HostWithCountingResizer()
    {
        var counter = new CountingResizer(new ImageSharpResizer(
            new ConfigurationBuilder().Build(), NullLogger<ImageSharpResizer>.Instance));

        var derived = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IImageResizer>();
                services.AddSingleton<IImageResizer>(counter);
            }));

        return (derived.CreateClient(), counter);
    }

    [Fact]
    public async Task A_requested_width_produces_a_resized_variant()
    {
        var token = await AdminTokenAsync();
        var original = Png(1200, 800);
        var id = await UploadAsync(token, isPublic: true, original);

        var response = await _client.GetAsync($"/api/public/files/{id}?w=320", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var bytes = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        WidthOf(bytes).Should().Be(320, "?w=320 is exactly a rung on the ladder, so it is served literally");
        bytes.Should().NotEqual(original, "and the original is 1200 wide, so these cannot be the same bytes");
    }

    [Fact]
    public async Task The_same_width_asked_for_twice_is_resized_once()
    {
        var token = await AdminTokenAsync();
        var id = await UploadAsync(token, isPublic: true, Png(1200, 800));

        var (client, counter) = HostWithCountingResizer();

        var first = await client.GetAsync($"/api/public/files/{id}?w=640", TestContext.Current.CancellationToken);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var firstBytes = await first.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);

        counter.Resizes.Should().Be(1, "the first request has nothing cached to reuse");

        var second = await client.GetAsync($"/api/public/files/{id}?w=640", TestContext.Current.CancellationToken);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        var secondBytes = await second.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);

        counter.Resizes.Should().Be(1, "the second request must come from the cache, not from the resizer");
        secondBytes.Should().Equal(firstBytes, "and it must be the same image, not an empty 200");

        var variants = await VariantsOfAsync(id);
        variants.Should().HaveCount(1, "one width asked for twice is one stored variant");
        variants[0].VariantWidth.Should().Be(640);
    }

    [Fact]
    public async Task A_width_over_the_cap_is_refused()
    {
        var token = await AdminTokenAsync();
        var id = await UploadAsync(token, isPublic: true, Png(1200, 800));

        var refused = await _client.GetAsync($"/api/public/files/{id}?w=99999", TestContext.Current.CancellationToken);

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "an uncapped width on an anonymous route is CPU and disk anyone can spend");

        var body = await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain("2048", "the refusal has to say what the cap is, or it cannot be acted on");

        (await VariantsOfAsync(id)).Should().BeEmpty("a refused width must not leave a stored blob behind");

        // The pairing. Refusing every width would satisfy the assertion above just as well.
        var allowed = await _client.GetAsync($"/api/public/files/{id}?w=320", TestContext.Current.CancellationToken);
        allowed.StatusCode.Should().Be(HttpStatusCode.OK, "a width inside the cap must still work");
        WidthOf(await allowed.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Should().Be(320);
    }

    [Fact]
    public async Task A_private_files_variant_is_not_anonymously_readable()
    {
        var token = await AdminTokenAsync();
        var id = await UploadAsync(token, isPublic: false, Png(1200, 800));

        var anonymous = await _client.GetAsync($"/api/public/files/{id}?w=320", TestContext.Current.CancellationToken);

        anonymous.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a width must not be a second way to the bytes of a file that is not public");

        (await VariantsOfAsync(id)).Should().BeEmpty(
            "and the refusal must happen before the resize, so an anonymous caller cannot make the "
          + "server work on a file it will not serve");

        // The pairing, on the route that is allowed to have it: the uploader still gets a variant.
        var authorised = await GetWithAuthAsync(_client, token, $"/api/files/{id}?w=320");
        authorised.StatusCode.Should().Be(HttpStatusCode.OK,
            await authorised.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        WidthOf(await authorised.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Should().Be(320);
    }

    [Fact]
    public async Task A_cached_variant_is_not_readable_by_its_own_id()
    {
        var token = await AdminTokenAsync();
        var id = await UploadAsync(token, isPublic: false, Png(1200, 800));

        var made = await GetWithAuthAsync(_client, token, $"/api/files/{id}?w=320");
        made.StatusCode.Should().Be(HttpStatusCode.OK);

        var variants = await VariantsOfAsync(id);
        variants.Should().HaveCount(1, "there is nothing to address if nothing was cached");

        // An admin passes CanRead on any record, so this 404 is the not-addressable rule and nothing
        // else. A variant that answered here would be a second record carrying its own copy of an
        // access decision, free to drift out of step with the file it came from.
        var direct = await GetWithAuthAsync(_client, token, $"/api/files/{variants[0].Id}");
        direct.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a variant is reached as ?w= on its original, never by an id of its own");

        // The pairing: the original this same admin uploaded is still readable by id.
        var parent = await GetWithAuthAsync(_client, token, $"/api/files/{id}");
        parent.StatusCode.Should().Be(HttpStatusCode.OK, "the original is still addressable");
    }

    [Fact]
    public async Task A_requested_width_is_snapped_onto_the_ladder()
    {
        var token = await AdminTokenAsync();
        var id = await UploadAsync(token, isPublic: true, Png(1200, 800));

        var response = await _client.GetAsync($"/api/public/files/{id}?w=400", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        WidthOf(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken))
            .Should().Be(640,
                "an arbitrary width honoured literally lets an anonymous caller walk w=1 to w=2048 "
              + "and leave two thousand blobs behind, so requests land on the rung at or above them");

        var variants = await VariantsOfAsync(id);
        variants.Should().HaveCount(1);
        variants[0].VariantWidth.Should().Be(640, "and the row records the rung, not what was asked for");
    }

    [Fact]
    public async Task A_pdf_asked_for_a_width_is_served_unchanged()
    {
        var token = await AdminTokenAsync();
        var pdf = System.Text.Encoding.ASCII.GetBytes("%PDF-1.4\nnot really a pdf, and nothing here parses it\n%%EOF");
        var id = await UploadAsync(token, isPublic: true, pdf, "application/pdf", "doc.pdf");

        var (client, counter) = HostWithCountingResizer();
        var response = await client.GetAsync($"/api/public/files/{id}?w=320", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "a frontend that puts ?w= on every asset URL must not get a 500 from the one that is a PDF");
        (await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Should().Equal(pdf);

        // Asserted on the resizer rather than only on the answer. A decoder handed a PDF happens to
        // fail and be caught, so "it returned the PDF" is also what a version with no content type
        // check at all produces, having first read the whole file out of storage to find out.
        counter.Resizes.Should().Be(0, "a type the resizer cannot decode must not reach it");

        (await VariantsOfAsync(id)).Should().BeEmpty("and nothing is cached for a type nobody can resize");
    }

    [Fact]
    public async Task An_image_already_narrower_than_the_request_is_served_unchanged()
    {
        var token = await AdminTokenAsync();
        var small = Png(100, 80);
        var id = await UploadAsync(token, isPublic: true, small);

        var response = await _client.GetAsync($"/api/public/files/{id}?w=640", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        (await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Should().Equal(small,
            "upscaling a 100px logo to 640 makes it blurrier and costs a row and a blob to keep");

        (await VariantsOfAsync(id)).Should().BeEmpty();
    }

    [Fact]
    public async Task With_variants_turned_off_a_width_is_ignored()
    {
        // The compatibility guarantee. MaxWidth zero has to mean the route answers exactly what it
        // answered before variants existed, for every URL, including one carrying a ?w=.
        var token = await AdminTokenAsync();
        var original = Png(1200, 800);
        var id = await UploadAsync(token, isPublic: true, original);

        var client = _factory.WithSetting($"{ImageVariantOptions.Section}:MaxWidth", "0").CreateClient();

        var response = await client.GetAsync($"/api/public/files/{id}?w=320", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Should().Equal(original);

        (await VariantsOfAsync(id)).Should().BeEmpty();
    }
}
