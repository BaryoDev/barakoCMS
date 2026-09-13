using Xunit;
using FluentAssertions;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using BarakoCMS.Files.S3;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Options;

namespace BarakoCMS.Tests;

/// <summary>
/// S3FileStorage against a real SeaweedFS container (S3-compatible), so the same code path that runs
/// against AWS S3 or Cloudflare R2 is exercised locally: put/get round-trip, a public file's URL,
/// a private file returning no URL, missing-key handling, and delete.
/// </summary>
public class S3FileStorageTests : IAsyncLifetime
{
    private const string AccessKey = "barako-test";
    private const string SecretKey = "barako-test-secret";
    private const string Bucket = "media";
    private const ushort S3Port = 8333;

    /*
     * SeaweedFS, not MinIO. MinIO archived its repository and publishes no more images or security
     * patches (#619). Pinned by digest so a retagged image cannot change what these tests run against.
     * `weed mini` runs master, volume, filer and the S3 gateway in one process, and the AWS_* variables
     * create its admin identity.
     */
    private readonly IContainer _s3Server = new ContainerBuilder(
            "chrislusf/seaweedfs:4.46@sha256:08d516132314207d10c8e37cbffc1f32b147d870169688734cc61c6231625b62")
        .WithCommand("mini")
        .WithEnvironment("AWS_ACCESS_KEY_ID", AccessKey)
        .WithEnvironment("AWS_SECRET_ACCESS_KEY", SecretKey)
        .WithPortBinding(S3Port, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilHttpRequestIsSucceeded(r => r.ForPort(S3Port).ForPath("/healthz")))
        .Build();

    private IAmazonS3 _s3 = null!;
    private S3FileStorage _storage = null!;

    public async ValueTask InitializeAsync()
    {
        await _s3Server.StartAsync();
        var endpoint = $"http://{_s3Server.Hostname}:{_s3Server.GetMappedPublicPort(S3Port)}";

        var cfg = new AmazonS3Config { ServiceURL = endpoint, ForcePathStyle = true };
        _s3 = new AmazonS3Client(new BasicAWSCredentials(AccessKey, SecretKey), cfg);
        await _s3.PutBucketAsync(Bucket);

        var opts = Options.Create(new S3StorageOptions
        {
            Bucket = Bucket,
            ServiceUrl = endpoint,
            AccessKey = AccessKey,
            SecretKey = SecretKey,
            ForcePathStyle = true,
            PublicBaseUrl = "https://cdn.example.com",
        });
        _storage = new S3FileStorage(_s3, opts);
    }

    public async ValueTask DisposeAsync() => await _s3Server.DisposeAsync();

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
