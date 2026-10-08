using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BarakoCMS.Files;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using barakoCMS.Models;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Visibility is in the storage key: <c>public/</c> or <c>private/</c>, for an upload and for a
/// resize of it.
/// </summary>
/// <remarks>
/// The prefix is what a bucket policy or a CDN origin can be scoped to, so a key on the wrong side
/// is a private file served by the bucket. A file stored before the prefixes keeps its flat key,
/// and every route still has to find it by that key.
/// </remarks>
[Collection("Sequential")]
public class FileKeyLayoutTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;

    public FileKeyLayoutTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task A_public_upload_is_stored_under_public_and_a_private_one_under_private()
    {
        var token = await AdminTokenAsync();
        var publicBytes = Png(40, 30);
        var privateBytes = Png(50, 30);

        var publicId = await UploadAsync(token, isPublic: true, publicBytes);
        var privateId = await UploadAsync(token, isPublic: false, privateBytes);

        var publicKey = (await LoadAsync(publicId)).StorageKey;
        var privateKey = (await LoadAsync(privateId)).StorageKey;

        publicKey.Should().StartWith("public/").And.EndWith(".png");
        privateKey.Should().StartWith("private/").And.EndWith(".png");
        publicKey["public/".Length..].Should().NotContain("/", "one segment under the prefix, nothing nested");
        privateKey["private/".Length..].Should().NotContain("/", "one segment under the prefix, nothing nested");

        // The key now carries a slash, and both routes read the bytes back by it.
        var served = await _client.GetAsync($"/api/public/files/{publicId}", TestContext.Current.CancellationToken);
        served.StatusCode.Should().Be(HttpStatusCode.OK);
        (await served.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Should().Equal(publicBytes);

        var authorised = await GetWithAuthAsync(token, $"/api/files/{privateId}");
        authorised.StatusCode.Should().Be(HttpStatusCode.OK);
        (await authorised.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Should().Equal(privateBytes);
    }

    [Theory]
    [InlineData(true, "public/")]
    [InlineData(false, "private/")]
    public async Task A_resize_is_stored_under_the_same_prefix_as_its_original(bool isPublic, string prefix)
    {
        var token = await AdminTokenAsync();
        var id = await UploadAsync(token, isPublic, Png(1200, 800));

        var resized = await GetWithAuthAsync(token, $"/api/files/{id}?w=320");
        resized.StatusCode.Should().Be(HttpStatusCode.OK,
            await resized.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var original = await LoadAsync(id);
        var variants = await VariantsOfAsync(id);

        variants.Should().HaveCount(1, "one width was asked for");
        variants[0].StorageKey.Should().StartWith(prefix);

        var stem = Path.GetFileNameWithoutExtension(original.StorageKey);
        variants[0].StorageKey.Should().Be($"{prefix}{stem}_w320.png");
    }

    /// <summary>
    /// A row stored before the prefixes: a flat key, and a public flag. Nothing moves it, and it is
    /// still downloaded, resized and deleted.
    /// </summary>
    [Fact]
    public async Task A_file_stored_under_the_flat_layout_is_still_served_resized_and_deleted()
    {
        var token = await AdminTokenAsync();
        var bytes = Png(1200, 800);
        var id = Guid.NewGuid();
        var flatKey = $"{id:N}.png";

        using (var scope = _factory.Services.CreateScope())
        {
            var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();
            var stored = await storage.PutAsync(
                new MemoryStream(bytes), flatKey, "image/png", true, TestContext.Current.CancellationToken);

            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new StoredFile
            {
                Id = id,
                FileName = "old.png",
                ContentType = "image/png",
                Size = bytes.Length,
                Provider = storage.Provider,
                StorageKey = stored.Key,
                IsPublic = true,
                PublicUrl = stored.PublicUrl,
                UploadedBy = Guid.NewGuid(),
                CreatedAt = DateTime.UtcNow,
            });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var served = await _client.GetAsync($"/api/public/files/{id}", TestContext.Current.CancellationToken);
        served.StatusCode.Should().Be(HttpStatusCode.OK);
        (await served.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Should().Equal(bytes);

        var resized = await _client.GetAsync($"/api/public/files/{id}?w=320", TestContext.Current.CancellationToken);
        resized.StatusCode.Should().Be(HttpStatusCode.OK,
            await resized.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        WidthOf(await resized.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Should().Be(320);

        (await LoadAsync(id)).StorageKey.Should().Be(flatKey, "serving and resizing an old file does not move it");

        var variants = await VariantsOfAsync(id);
        variants.Should().HaveCount(1, "one width was asked for");
        variants[0].StorageKey.Should().Be($"public/{id:N}_w320.png",
            "a new object is written under the prefix even when its original has none");

        using var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/files/{id}");
        delete.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var deleted = await _client.SendAsync(delete, TestContext.Current.CancellationToken);
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent,
            await deleted.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        using (var scope = _factory.Services.CreateScope())
        {
            var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();
            (await storage.GetAsync(flatKey, TestContext.Current.CancellationToken))
                .Should().BeNull("the delete finds the old object by the key on its row");
            (await storage.GetAsync(variants[0].StorageKey, TestContext.Current.CancellationToken))
                .Should().BeNull("and the resize by the key on its own row");
        }
    }

    /// <summary>
    /// The prefix of a resize comes from the original's flag, and only the last segment of the
    /// original's key is carried over. A private original whose key sits under <c>public/</c>, or
    /// climbs out of its folder, still gets a resize under <c>private/</c>.
    /// </summary>
    [Theory]
    [InlineData("public/{0}.png")]
    [InlineData("../public/{0}.png")]
    [InlineData("a/b/{0}.png")]
    [InlineData("{0}.png")]
    public async Task A_resize_of_a_private_file_lands_under_private_whatever_the_originals_key_is(string keyShape)
    {
        var storage = new RecordingStorage();
        var id = Guid.NewGuid();
        var parentKey = string.Format(keyShape, id.ToString("N"));
        var bytes = Png(1200, 800);

        await storage.PutAsync(new MemoryStream(bytes), parentKey, "image/png", false, TestContext.Current.CancellationToken);

        var original = new StoredFile
        {
            Id = id,
            FileName = "pic.png",
            ContentType = "image/png",
            Size = bytes.Length,
            Provider = storage.Provider,
            StorageKey = parentKey,
            IsPublic = false,
            UploadedBy = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
        };

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(original);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        var variants = new ImageVariants(
            session,
            storage,
            new SkiaImageResizer(new ConfigurationBuilder().Build(), NullLogger<SkiaImageResizer>.Instance),
            new ConfigurationBuilder().Build());

        var resolved = await variants.ResolveAsync(original, 320, TestContext.Current.CancellationToken);

        resolved.Refused.Should().BeNull();
        resolved.File.Id.Should().NotBe(original.Id, "a resize happened, so there is a key to assert on");

        storage.Puts.Should().HaveCount(2, "the seed put the original and the resize put the variant");
        storage.Puts[1].Key.Should().Be($"private/{id:N}_w320.png");
        storage.Puts[1].IsPublic.Should().BeFalse();
        resolved.File.StorageKey.Should().Be($"private/{id:N}_w320.png");
    }

    private sealed class RecordingStorage : IFileStorage
    {
        private readonly Dictionary<string, byte[]> _blobs = new();

        public string Provider => "recording";

        public List<(string Key, bool IsPublic)> Puts { get; } = new();

        public Task<StoredObjectRef> PutAsync(
            Stream content, string key, string contentType, bool isPublic, CancellationToken ct = default)
        {
            using var buffer = new MemoryStream();
            content.CopyTo(buffer);
            _blobs[key] = buffer.ToArray();
            Puts.Add((key, isPublic));
            return Task.FromResult(new StoredObjectRef(key, PublicUrl(key, isPublic)));
        }

        public Task<byte[]?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(_blobs.TryGetValue(key, out var bytes) ? bytes : null);

        public string? PublicUrl(string key, bool isPublic) =>
            isPublic ? $"https://bucket.example.com/{key}" : null;

        public Task DeleteAsync(string key, CancellationToken ct = default)
        {
            _blobs.Remove(key);
            return Task.CompletedTask;
        }
    }

    private static byte[] Png(int width, int height) => FileSamples.Noise(width, height);

    private static int WidthOf(byte[] bytes) => FileSamples.WidthOf(bytes);

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

    private async Task<Guid> UploadAsync(string token, bool isPublic, byte[] bytes)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "pic.png");
        form.Add(new StringContent(isPublic ? "true" : "false"), "isPublic");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/files") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.Created,
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var body = await response.Content.ReadFromJsonAsync<UploadResponse>(TestContext.Current.CancellationToken);
        return body!.Id;
    }

    private async Task<HttpResponseMessage> GetWithAuthAsync(string token, string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task<StoredFile> LoadAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var file = await scope.ServiceProvider.GetRequiredService<IQuerySession>()
            .LoadAsync<StoredFile>(id, TestContext.Current.CancellationToken);

        return file!;
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
}
