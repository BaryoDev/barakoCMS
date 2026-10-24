using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using BarakoCMS.Files;
using BarakoCMS.Files.S3;
using BarakoCMS.Files.Storage;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// A tenant chooses where its uploads are stored through <c>/api/settings/storage</c>, tests a
/// bucket before saving it, and switches without a restart (#781). Against a real SeaweedFS.
/// </summary>
[Collection("IntegrationSerial")]
public class StorageSettingsTests : IAsyncLifetime
{
    private const string AccessKey = "barako-test";
    private const string SecretKey = "barako-test-secret-781";
    private const string Bucket = "tenant-media";
    private const ushort S3Port = 8333;
    private const string Route = "/api/settings/storage";

    private static readonly Lock Hosts = new();
    private static WebApplicationFactory<Program>? _privateNetworkHost;
    private static WebApplicationFactory<Program>? _configuredHost;
    private static WebApplicationFactory<Program>? _configuredBucketHost;

    private readonly IntegrationTestFixture _factory;

    /// <summary>
    /// One server for the class rather than one per test, since xUnit builds the class per test.
    /// Testcontainers removes it when the run ends.
    /// </summary>
    private static readonly Lazy<Task<IContainer>> Server = new(StartServerAsync);

    private static async Task<IContainer> StartServerAsync()
    {
        var server = new ContainerBuilder(
            "chrislusf/seaweedfs:4.46@sha256:08d516132314207d10c8e37cbffc1f32b147d870169688734cc61c6231625b62")
            .WithCommand("mini")
            .WithEnvironment("AWS_ACCESS_KEY_ID", AccessKey)
            .WithEnvironment("AWS_SECRET_ACCESS_KEY", SecretKey)
            .WithPortBinding(S3Port, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r.ForPort(S3Port).ForPath("/healthz")))
            .Build();
        await server.StartAsync();
        return server;
    }

    private string _endpoint = string.Empty;
    private IAmazonS3 _s3 = null!;

    public StorageSettingsTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var server = await Server.Value;
        _endpoint = $"http://{server.Hostname}:{server.GetMappedPublicPort(S3Port)}";
        _s3 = new AmazonS3Client(new BasicAWSCredentials(AccessKey, SecretKey),
            new AmazonS3Config { ServiceURL = _endpoint, ForcePathStyle = true });
        if (!await Amazon.S3.Util.AmazonS3Util.DoesS3BucketExistV2Async(_s3, Bucket))
        {
            await _s3.PutBucketAsync(Bucket);
        }
    }

    public ValueTask DisposeAsync()
    {
        _s3.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>A host that lets a saved bucket sit on loopback, where the test container is.</summary>
    private WebApplicationFactory<Program> PrivateNetworkHost() =>
        Derived(ref _privateNetworkHost, S3ObjectStorageProvider.AllowPrivateServiceUrlKey, "true");

    /// <summary>A host whose configuration sets one storage key, so the API's setting is read-only.</summary>
    private WebApplicationFactory<Program> ConfiguredHost() =>
        Derived(ref _configuredHost, "Modules:Files.S3:Region", "eu-west-1");

    /// <summary>A host whose configuration sets a whole bucket, the way a deployment does today.</summary>
    private WebApplicationFactory<Program> ConfiguredBucketHost() => Derived(ref _configuredBucketHost, new()
    {
        ["Modules:Files.S3:Bucket"] = Bucket,
        ["Modules:Files.S3:ServiceUrl"] = _endpoint,
        ["Modules:Files.S3:AccessKey"] = AccessKey,
        ["Modules:Files.S3:SecretKey"] = SecretKey,
        ["Modules:Files.S3:ForcePathStyle"] = "true",
    });

    private WebApplicationFactory<Program> Derived(ref WebApplicationFactory<Program>? host, string key, string value) =>
        Derived(ref host, new Dictionary<string, string?> { [key] = value });

    private WebApplicationFactory<Program> Derived(ref WebApplicationFactory<Program>? host, Dictionary<string, string?> settings)
    {
        lock (Hosts)
        {
            if (host is null)
            {
                using var resolver = FastEndpointsResolver.Keep();
                host = _factory.WithSettings(settings);
                _ = host.Server;
            }

            return host;
        }
    }

    /// <summary>A SuperAdmin acting in <paramref name="tenant"/>, named by the header the way a console does.</summary>
    private async Task<HttpClient> SuperAdminInAsync(string tenant)
    {
        var userId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new barakoCMS.Models.User
            {
                Id = userId,
                Username = $"storage-{userId:N}",
                Email = $"storage-{userId:N}@example.com",
                RoleIds = [barakoCMS.Models.SystemRoles.SuperAdminRoleId],
            });
            session.Store(new barakoCMS.Models.Tenant { Id = Guid.NewGuid(), Slug = tenant, Name = tenant, Status = barakoCMS.Models.TenantStatus.Active });
            await session.SaveChangesAsync(Ct);
        }

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(["SuperAdmin"], userId.ToString()));
        client.DefaultRequestHeaders.Add("X-Tenant", tenant);
        return client;
    }

    private static async Task<HttpClient> ClientAsync(WebApplicationFactory<Program> host, Func<Task<string>> token)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await token());
        return client;
    }

    private Task<HttpClient> AdminAsync(WebApplicationFactory<Program> host) =>
        ClientAsync(host, () => _factory.StoredUserTokenAsync("SuperAdmin"));

    private object Bucketed(string? secret = SecretKey, string provider = StorageProviders.S3) => new
    {
        provider,
        s3 = new
        {
            bucket = Bucket,
            region = "us-east-1",
            serviceUrl = _endpoint,
            forcePathStyle = true,
            accessKey = AccessKey,
            secretKey = secret,
        },
    };

    private static async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpClient client, HttpMethod method, string route, object? body = null)
    {
        using var request = new HttpRequestMessage(method, route);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        var response = await client.SendAsync(request, Ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    private static JsonElement Json(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }

    private static async Task<Guid> UploadAsync(HttpClient client, byte[] bytes)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "pic.png");
        var response = await client.PostAsync("/api/files", form, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
        return Json(body).GetProperty("id").GetGuid();
    }

    private async Task<StoredFile> FileAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<StoredFile>(id, Ct))!;
    }

    private async Task<string?> StoredSettingsJsonAsync()
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IQuerySession>().Json
            .FindByIdAsync<StorageSettings>(StorageSettings.SingletonId, Ct);
    }

    private async Task ForgetSettingsAsync()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Delete<StorageSettings>(StorageSettings.SingletonId);

            // A bucket file outlives the settings that can read it, so the files go with them and
            // no later test meets a record it cannot download.
            session.DeleteWhere<StoredFile>(f => f.Provider == StorageProviders.S3);
            await session.SaveChangesAsync(Ct);
        }

        foreach (var host in new[] { _privateNetworkHost, _configuredHost, _configuredBucketHost })
        {
            if (host is null)
            {
                continue;
            }

            using var scope = host.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<FileStorageResolver>().Forget();
        }

        using var main = _factory.Services.CreateScope();
        main.ServiceProvider.GetRequiredService<FileStorageResolver>().Forget();
    }

    [Fact]
    public async Task A_tenant_starts_on_postgres_with_a_bucket_on_offer_and_nothing_secret()
    {
        await ForgetSettingsAsync();
        var admin = await AdminAsync(_factory);

        var (status, body) = await SendAsync(admin, HttpMethod.Get, Route);

        status.Should().Be(HttpStatusCode.OK, body);
        var settings = Json(body);
        settings.GetProperty("provider").GetString().Should().Be("postgres");
        settings.GetProperty("activeProvider").GetString().Should().Be("postgres");
        settings.GetProperty("readOnly").GetBoolean().Should().BeFalse();
        settings.GetProperty("environmentKeys").GetArrayLength().Should().Be(0);
        settings.GetProperty("availableProviders").EnumerateArray().Select(p => p.GetString())
            .Should().Equal("postgres", "s3");
        settings.GetProperty("s3").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_saved_bucket_takes_the_next_upload_without_a_restart_and_older_files_still_download()
    {
        await ForgetSettingsAsync();
        try
        {
            var admin = await AdminAsync(PrivateNetworkHost());
            var before = FileSamples.Noise(40, 30);
            var inPostgres = await UploadAsync(admin, before);
            (await FileAsync(inPostgres)).Provider.Should().Be("postgres");

            var (saved, savedBody) = await SendAsync(admin, HttpMethod.Put, Route, Bucketed());
            saved.Should().Be(HttpStatusCode.OK, savedBody);
            Json(savedBody).GetProperty("activeProvider").GetString().Should().Be("s3");

            var after = FileSamples.Noise(50, 30);
            var inBucket = await UploadAsync(admin, after);
            var stored = await FileAsync(inBucket);
            stored.Provider.Should().Be("s3");
            using (var direct = await _s3.GetObjectAsync(Bucket, stored.StorageKey, Ct))
            {
                direct.ContentLength.Should().Be(after.Length, "the bytes are in the tenant's bucket");
            }

            (await admin.GetByteArrayAsync($"/api/files/{inPostgres}", Ct)).Should().Equal(before,
                "a file stored before the switch is read from where it was stored");
            (await admin.GetByteArrayAsync($"/api/files/{inBucket}", Ct)).Should().Equal(after);

            var (moved, movedBody) = await SendAsync(admin, HttpMethod.Put, Route, new
            {
                provider = "s3",
                s3 = new { bucket = "another-bucket", region = "us-east-1", serviceUrl = _endpoint, forcePathStyle = true, accessKey = AccessKey },
            });
            moved.Should().Be(HttpStatusCode.Conflict, "a file is in the bucket, and pointing the settings elsewhere would orphan it");
            movedBody.Should().Contain("s3.bucket");

            var (back, backBody) = await SendAsync(admin, HttpMethod.Put, Route, new { provider = "postgres" });
            back.Should().Be(HttpStatusCode.OK, backBody);
            var postgresAgain = await UploadAsync(admin, FileSamples.Noise(60, 30));
            (await FileAsync(postgresAgain)).Provider.Should().Be("postgres");
            (await admin.GetByteArrayAsync($"/api/files/{inBucket}", Ct)).Should().Equal(after,
                "switching back keeps the bucket's settings, so its files still download");

            (await admin.DeleteAsync($"/api/files/{inBucket}", Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
            var gone = () => _s3.GetObjectMetadataAsync(Bucket, stored.StorageKey, Ct);
            await gone.Should().ThrowAsync<AmazonS3Exception>("the delete went to the bucket the file is in, not to Postgres");

            foreach (var body in new[] { savedBody, backBody, (await SendAsync(admin, HttpMethod.Get, Route)).Body })
            {
                body.Should().NotContain(SecretKey);
            }

            var json = await StoredSettingsJsonAsync();
            json.Should().Contain(AccessKey, "the stored document is the one this test saved");
            json.Should().NotContain(SecretKey, "the secret is stored encrypted");
            Json(backBody).GetProperty("s3").GetProperty("secretSet").GetBoolean().Should().BeTrue();
        }
        finally
        {
            await ForgetSettingsAsync();
        }
    }

    [Fact]
    public async Task A_test_with_a_wrong_secret_fails_at_put_and_saves_nothing()
    {
        await ForgetSettingsAsync();
        var admin = await AdminAsync(PrivateNetworkHost());

        var (good, goodBody) = await SendAsync(admin, HttpMethod.Post, Route + "/test", Bucketed());
        good.Should().Be(HttpStatusCode.OK, goodBody);
        Json(goodBody).GetProperty("ok").GetBoolean().Should().BeTrue("the positive control: the right secret works");

        var (status, body) = await SendAsync(admin, HttpMethod.Post, Route + "/test", Bucketed(secret: "not-the-secret"));

        status.Should().Be(HttpStatusCode.OK, body);
        var result = Json(body);
        result.GetProperty("ok").GetBoolean().Should().BeFalse();
        result.GetProperty("step").GetString().Should().Be("put");
        result.GetProperty("errorCode").GetString().Should().NotBeNullOrEmpty();
        body.Should().NotContain("not-the-secret").And.NotContain(SecretKey);
        goodBody.Should().NotContain(SecretKey);
        (await StoredSettingsJsonAsync()).Should().BeNull("testing saves nothing");
    }

    [Fact]
    public async Task By_default_a_bucket_on_a_private_address_is_refused_when_the_connection_opens()
    {
        await ForgetSettingsAsync();
        var admin = await AdminAsync(_factory);

        var (status, body) = await SendAsync(admin, HttpMethod.Post, Route + "/test", Bucketed());

        status.Should().Be(HttpStatusCode.OK, body);
        var result = Json(body);
        result.GetProperty("ok").GetBoolean().Should().BeFalse(
            "the same settings pass on a host that allows private addresses, so this is the address policy");
        result.GetProperty("step").GetString().Should().Be("put");
        result.GetProperty("message").GetString().Should().Contain("blocked");
    }

    [Fact]
    public async Task Storage_set_in_configuration_makes_the_setting_read_only_and_names_the_keys()
    {
        await ForgetSettingsAsync();
        var admin = await AdminAsync(ConfiguredHost());

        var (got, gotBody) = await SendAsync(admin, HttpMethod.Get, Route);
        got.Should().Be(HttpStatusCode.OK, gotBody);
        Json(gotBody).GetProperty("readOnly").GetBoolean().Should().BeTrue();
        Json(gotBody).GetProperty("environmentKeys").EnumerateArray().Select(k => k.GetString()).Should().Equal("Region");

        var (status, body) = await SendAsync(admin, HttpMethod.Put, Route, Bucketed());

        status.Should().Be(HttpStatusCode.Conflict);
        body.Should().Contain("Region").And.Contain("Modules:Files.S3");
        (await StoredSettingsJsonAsync()).Should().BeNull();
    }

    [Fact]
    public async Task Every_route_needs_a_token_and_manage_storage()
    {
        await ForgetSettingsAsync();
        var admin = await AdminAsync(_factory);
        (await SendAsync(admin, HttpMethod.Get, Route)).Status.Should().Be(HttpStatusCode.OK,
            "the positive control: the capability opens the route");

        var anonymous = _factory.CreateClient();
        (await SendAsync(anonymous, HttpMethod.Get, Route)).Status.Should().Be(HttpStatusCode.Unauthorized);

        var user = await ClientAsync(_factory, () => _factory.StoredUserTokenAsync("User"));
        (await SendAsync(user, HttpMethod.Get, Route)).Status.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(user, HttpMethod.Put, Route, Bucketed())).Status.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(user, HttpMethod.Post, Route + "/test", Bucketed())).Status.Should().Be(HttpStatusCode.Forbidden);
        (await StoredSettingsJsonAsync()).Should().BeNull("a refused PUT saves nothing");
    }

    [Fact]
    public async Task A_bucket_without_a_secret_is_refused()
    {
        await ForgetSettingsAsync();
        var admin = await AdminAsync(PrivateNetworkHost());

        var (status, body) = await SendAsync(admin, HttpMethod.Put, Route, Bucketed(secret: null));

        status.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("secretKey");
        (await StoredSettingsJsonAsync()).Should().BeNull();
    }

    [Fact]
    public async Task A_deployment_that_configures_a_bucket_keeps_storing_there_and_cannot_be_changed_here()
    {
        await ForgetSettingsAsync();
        try
        {
            var admin = await AdminAsync(ConfiguredBucketHost());

            var (got, gotBody) = await SendAsync(admin, HttpMethod.Get, Route);
            got.Should().Be(HttpStatusCode.OK, gotBody);
            var settings = Json(gotBody);
            settings.GetProperty("readOnly").GetBoolean().Should().BeTrue();
            settings.GetProperty("activeProvider").GetString().Should().Be("s3");
            settings.GetProperty("environmentKeys").EnumerateArray().Select(k => k.GetString())
                .Should().BeEquivalentTo("Bucket", "ServiceUrl", "AccessKey", "SecretKey", "ForcePathStyle");
            gotBody.Should().NotContain(SecretKey);

            var bytes = FileSamples.Noise(30, 20);
            var id = await UploadAsync(admin, bytes);
            var stored = await FileAsync(id);
            stored.Provider.Should().Be("s3");
            (await _s3.GetObjectMetadataAsync(Bucket, stored.StorageKey, Ct)).ContentLength.Should().Be(bytes.Length,
                "the configured bucket serves uploads exactly as before tenants could choose");
            (await admin.GetByteArrayAsync($"/api/files/{id}", Ct)).Should().Equal(bytes);

            (await SendAsync(admin, HttpMethod.Put, Route, new { provider = "postgres" })).Status
                .Should().Be(HttpStatusCode.Conflict);
        }
        finally
        {
            await ForgetSettingsAsync();
        }
    }

    [Fact]
    public async Task One_tenants_saved_bucket_is_not_another_tenants()
    {
        await ForgetSettingsAsync();
        var tenantA = $"st-a-{Guid.NewGuid():N}"[..14];
        var tenantB = $"st-b-{Guid.NewGuid():N}"[..14];
        var inA = await SuperAdminInAsync(tenantA);
        var inB = await SuperAdminInAsync(tenantB);

        var (saved, savedBody) = await SendAsync(inA, HttpMethod.Put, Route, new
        {
            provider = "s3",
            s3 = new { bucket = Bucket, region = "us-east-1", serviceUrl = "https://storage.example.com", forcePathStyle = true, accessKey = AccessKey, secretKey = SecretKey },
        });
        saved.Should().Be(HttpStatusCode.OK, savedBody);
        var (aGot, aBody) = await SendAsync(inA, HttpMethod.Get, Route);
        aGot.Should().Be(HttpStatusCode.OK, aBody);
        Json(aBody).GetProperty("provider").GetString().Should().Be("s3", "the positive control: tenant A saved a bucket");

        var (bGot, bBody) = await SendAsync(inB, HttpMethod.Get, Route);

        bGot.Should().Be(HttpStatusCode.OK, bBody);
        Json(bBody).GetProperty("provider").GetString().Should().Be("postgres");
        Json(bBody).GetProperty("activeProvider").GetString().Should().Be("postgres");
        Json(bBody).GetProperty("s3").ValueKind.Should().Be(JsonValueKind.Null);
        bBody.Should().NotContain("storage.example.com");
    }

    [Fact]
    public async Task A_secret_that_cannot_be_decrypted_is_reported_and_uploads_stay_in_postgres()
    {
        await ForgetSettingsAsync();
        try
        {
            using (var scope = _factory.Services.CreateScope())
            {
                var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
                session.Store(new StorageSettings
                {
                    Provider = StorageProviders.S3,
                    S3 = new ObjectStorageSettings
                    {
                        Bucket = Bucket,
                        ServiceUrl = _endpoint,
                        AccessKey = AccessKey,
                        ProtectedSecretKey = "v1:not-something-this-key-wrote",
                    },
                });
                await session.SaveChangesAsync(Ct);
            }

            var admin = await AdminAsync(PrivateNetworkHost());
            using (var scope = PrivateNetworkHost().Services.CreateScope())
            {
                scope.ServiceProvider.GetRequiredService<FileStorageResolver>().Forget();
            }

            var (got, gotBody) = await SendAsync(admin, HttpMethod.Get, Route);
            got.Should().Be(HttpStatusCode.OK, gotBody);
            var s3 = Json(gotBody).GetProperty("s3");
            s3.GetProperty("secretSet").GetBoolean().Should().BeTrue();
            s3.GetProperty("secretUnreadable").GetBoolean().Should().BeTrue();
            Json(gotBody).GetProperty("activeProvider").GetString().Should().Be("postgres");

            var id = await UploadAsync(admin, FileSamples.Noise(20, 20));
            (await FileAsync(id)).Provider.Should().Be("postgres");

            var (tested, testedBody) = await SendAsync(admin, HttpMethod.Post, Route + "/test", Bucketed(secret: null));
            tested.Should().Be(HttpStatusCode.BadRequest);
            testedBody.Should().Contain("could not be decrypted");
        }
        finally
        {
            await ForgetSettingsAsync();
        }
    }
}
