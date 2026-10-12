using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using BarakoCMS.Files.Storage;
using Microsoft.Extensions.Options;

namespace BarakoCMS.Files.S3;

/// <summary>
/// Builds S3 storage from a tenant's saved settings, and tests a set of settings before they are
/// saved.
/// </summary>
/// <remarks>
/// <para>
/// A service URL typed into the API is an address the server will call, so by default the client
/// connects only to public addresses, under the same policy webhooks follow: a bucket on loopback,
/// a private network or a cloud metadata address is refused when the socket opens.
/// <see cref="AllowPrivateServiceUrlKey"/> lifts that for a deployment whose object store sits on
/// its own private network, where whoever holds <c>manage_storage</c> is trusted with that network.
/// Storage set in configuration is not affected: the operator chose that address.
/// </para>
/// <para>
/// Clients are kept per set of settings, since building one costs a handshake per request. A change
/// to any setting, the secret included, is a different set and a new client.
/// </para>
/// </remarks>
internal sealed class S3ObjectStorageProvider(bool allowPrivateServiceUrl) : IObjectStorageProvider
{
    /// <summary>The key in this module's section, <c>Modules:Files.S3</c>.</summary>
    public const string AllowPrivateServiceUrlSetting = "AllowPrivateServiceUrl";

    public const string AllowPrivateServiceUrlKey = "Modules:Files.S3:" + AllowPrivateServiceUrlSetting;

    private const int MaxClients = 128;
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(20);

    private readonly ConcurrentDictionary<string, IAmazonS3> _clients = new(StringComparer.Ordinal);

    public string Provider => StorageProviders.S3;

    public IFileStorage Create(ObjectStorageSettings settings, string secretKey)
    {
        var fingerprint = Fingerprint(settings, secretKey);
        if (!_clients.TryGetValue(fingerprint, out var client))
        {
            // A cap rather than an eviction policy: settings change rarely, so reaching it means
            // something is churning, and starting over is cheaper than tracking which are in use.
            if (_clients.Count >= MaxClients)
            {
                _clients.Clear();
            }

            client = _clients.GetOrAdd(fingerprint, _ => Client(settings, secretKey, forTest: false));
        }

        return new S3FileStorage(client, Microsoft.Extensions.Options.Options.Create(StorageOptionsFor(settings, secretKey)));
    }

    public async Task<StorageTestResult> TestAsync(ObjectStorageSettings settings, string secretKey, CancellationToken ct)
    {
        using var client = Client(settings, secretKey, forTest: true);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TestTimeout);

        var key = $"barako-storage-test/{Guid.NewGuid():N}.txt";
        var body = "barakoCMS storage test " + key;
        var step = "put";
        try
        {
            await client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = settings.Bucket,
                Key = key,
                ContentBody = body,
                ContentType = "text/plain",
            }, timeout.Token);

            step = "get";
            using (var read = await client.GetObjectAsync(settings.Bucket, key, timeout.Token))
            using (var reader = new StreamReader(read.ResponseStream))
            {
                if (await reader.ReadToEndAsync(timeout.Token) != body)
                {
                    return new StorageTestResult(false, step, null, "The object read back is not the one written.");
                }
            }

            step = "delete";
            await client.DeleteObjectAsync(settings.Bucket, key, timeout.Token);
            return new StorageTestResult(true, null, null, "Wrote, read back and deleted a test object.");
        }
        catch (AmazonS3Exception ex)
        {
            return new StorageTestResult(false, step, ex.ErrorCode, ex.Message);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new StorageTestResult(false, step, "Timeout", $"No answer within {TestTimeout.TotalSeconds:0} seconds.");
        }
        catch (Exception ex) when (ex is AmazonClientException or HttpRequestException or IOException)
        {
            // The connection itself: a name that does not resolve, a refused port, or an address
            // the outbound policy refuses. The innermost message is the one that says which.
            return new StorageTestResult(false, step, null, Innermost(ex).Message);
        }
    }

    private IAmazonS3 Client(ObjectStorageSettings settings, string secretKey, bool forTest)
    {
        var config = new AmazonS3Config
        {
            ForcePathStyle = settings.ForcePathStyle,
            MaxErrorRetry = forTest ? 0 : S3StorageOptions.DefaultMaxErrorRetry,
            Timeout = forTest ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(S3StorageOptions.DefaultTimeoutSeconds),
        };

        if (!string.IsNullOrEmpty(settings.ServiceUrl))
        {
            config.ServiceURL = settings.ServiceUrl;
        }
        else
        {
            config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(settings.Region);
        }

        if (!allowPrivateServiceUrl)
        {
            config.HttpClientFactory = new GuardedHttpClientFactory();
        }

        return new AmazonS3Client(new BasicAWSCredentials(settings.AccessKey, secretKey), config);
    }

    private static S3StorageOptions StorageOptionsFor(ObjectStorageSettings settings, string secretKey) => new()
    {
        Bucket = settings.Bucket,
        Region = settings.Region,
        ServiceUrl = settings.ServiceUrl,
        PublicBaseUrl = settings.PublicBaseUrl,
        UsePublicReadAcl = settings.UsePublicReadAcl,
        ForcePathStyle = settings.ForcePathStyle,
        AccessKey = settings.AccessKey,
        SecretKey = secretKey,
    };

    private static string Fingerprint(ObjectStorageSettings s, string secretKey)
    {
        var text = string.Join('\u001f', s.Bucket, s.Region, s.ServiceUrl, s.PublicBaseUrl,
            s.UsePublicReadAcl, s.ForcePathStyle, s.AccessKey, secretKey);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static Exception Innermost(Exception ex)
    {
        while (ex.InnerException is not null)
        {
            ex = ex.InnerException;
        }

        return ex;
    }

    private sealed class GuardedHttpClientFactory : HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig) =>
            new(barakoCMS.Infrastructure.Http.OutboundConnections.GuardedHandler(), disposeHandler: true);

        // The SDK asks for an HttpClient on every request unless it may cache the one it got, and a
        // client per request is a handshake per request and a handler never disposed.
        public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => true;

        public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => false;

        // Null caches per S3 client, so the HttpClient goes when its S3 client is disposed.
        public override string? GetConfigUniqueString(IClientConfig clientConfig) => null;
    }
}

/// <summary>The <c>Files:S3</c> keys this deployment sets in configuration.</summary>
internal sealed class ConfiguredStorageKeys(IReadOnlyList<string> keys) : IStorageEnvironment
{
    public IReadOnlyList<string> Keys { get; } = keys;
}
