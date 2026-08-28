using Xunit;
using FluentAssertions;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using BarakoCMS.Files.S3;
using Microsoft.Extensions.Options;
using Testcontainers.Minio;

namespace BarakoCMS.Tests;

/// <summary>
/// S3FileStorage against a real MinIO container (S3-compatible), so the same code path that runs
/// against AWS S3 or Cloudflare R2 is exercised locally: put/get round-trip, a public file's URL,
/// a private file returning no URL, missing-key handling, and delete.
/// </summary>
public class S3FileStorageTests : IAsyncLifetime
{
    private const string User = "minioadmin";
    private const string Pass = "minioadmin-secret";
    private const string Bucket = "media";

    private readonly MinioContainer _minio = new MinioBuilder()
        .WithImage("minio/minio:RELEASE.2024-01-16T16-07-38Z")
        .WithUsername(User)
        .WithPassword(Pass)
        .Build();

    private IAmazonS3 _s3 = null!;
    private S3FileStorage _storage = null!;

    public async ValueTask InitializeAsync()
    {
        await _minio.StartAsync();
        var endpoint = _minio.GetConnectionString();

        var cfg = new AmazonS3Config { ServiceURL = endpoint, ForcePathStyle = true };
        _s3 = new AmazonS3Client(new BasicAWSCredentials(User, Pass), cfg);
        await _s3.PutBucketAsync(Bucket);

        var opts = Options.Create(new S3StorageOptions
        {
            Bucket = Bucket,
            ServiceUrl = endpoint,
            AccessKey = User,
            SecretKey = Pass,
            ForcePathStyle = true,
            PublicBaseUrl = "https://cdn.example.com",
        });
        _storage = new S3FileStorage(_s3, opts);
    }

    public async ValueTask DisposeAsync() => await _minio.DisposeAsync();

    private static Stream Bytes(string s) => new MemoryStream(Encoding.UTF8.GetBytes(s));

    [Fact]
    public async Task Put_then_Get_roundtrips()
    {
        await _storage.PutAsync(Bytes("hello world"), "a/one.txt", "text/plain", isPublic: false);
        var got = await _storage.GetAsync("a/one.txt");
        Encoding.UTF8.GetString(got!).Should().Be("hello world");
    }

    [Fact]
    public async Task Put_public_returns_a_public_url()
    {
        var r = await _storage.PutAsync(Bytes("img"), "pub/pic.png", "image/png", isPublic: true);
        r.PublicUrl.Should().Be("https://cdn.example.com/pub/pic.png");
    }

    [Fact]
    public async Task Put_private_returns_no_public_url()
    {
        var r = await _storage.PutAsync(Bytes("secret"), "priv/doc.pdf", "application/pdf", isPublic: false);
        r.PublicUrl.Should().BeNull("private files are not publicly addressable");
    }

    [Fact]
    public async Task Get_missing_key_returns_null()
    {
        (await _storage.GetAsync("nope/missing.bin")).Should().BeNull();
    }

    [Fact]
    public async Task Delete_removes_the_object()
    {
        await _storage.PutAsync(Bytes("x"), "del/me.txt", "text/plain", isPublic: false);
        await _storage.DeleteAsync("del/me.txt");
        (await _storage.GetAsync("del/me.txt")).Should().BeNull();
    }
}
