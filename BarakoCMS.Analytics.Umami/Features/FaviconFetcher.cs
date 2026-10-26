using Microsoft.Extensions.Caching.Memory;

namespace BarakoCMS.Analytics.Umami.Features;

public sealed record Favicon(byte[] Bytes, string ContentType);

/// <summary>
/// Fetches a referrer's favicon on the server so the console never contacts a third party itself.
/// </summary>
/// <remarks>
/// It goes through the named "ExternalApi" client, whose connect callback refuses private addresses
/// and which follows no redirects, so a 3xx is a miss. The body is read with a bound rather than
/// trusting Content-Length. Hits and misses are both cached for a day, in a cache with a byte limit,
/// so a stream of distinct domains cannot grow memory without bound. SVG is not on the allowlist
/// because it can carry script.
/// </remarks>
public sealed class FaviconFetcher(IHttpClientFactory httpClientFactory) : IDisposable
{
    public const int MaxBytes = 64 * 1024;
    private const long CacheBytes = 8 * 1024 * 1024;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(1);

    private static readonly HashSet<string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/x-icon", "image/vnd.microsoft.icon", "image/png", "image/gif", "image/jpeg", "image/webp",
    };

    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = CacheBytes });

    private sealed record Entry(Favicon? Icon);

    /// <param name="host">A hostname that passed <see cref="FaviconHostname.TryNormalize"/>.</param>
    public async Task<Favicon?> GetAsync(string host, CancellationToken ct)
    {
        if (_cache.TryGetValue(host, out Entry? cached) && cached is not null)
        {
            return cached.Icon;
        }

        var icon = await FetchAsync(host, ct);
        _cache.Set(host, new Entry(icon), new MemoryCacheEntryOptions
        {
            Size = Math.Max(1, icon?.Bytes.Length ?? 1),
            AbsoluteExpirationRelativeToNow = Lifetime,
        });
        return icon;
    }

    private async Task<Favicon?> FetchAsync(string host, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);

        try
        {
            var client = httpClientFactory.CreateClient("ExternalApi");
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{host}/favicon.ico");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode != System.Net.HttpStatusCode.OK)
            {
                return null;
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is null || !ImageTypes.Contains(mediaType))
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var buffer = new byte[MaxBytes + 1];
            var read = 0;
            while (read < buffer.Length)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(read), timeout.Token);
                if (n == 0)
                {
                    break;
                }

                read += n;
            }

            return read is 0 or > MaxBytes ? null : new Favicon(buffer[..read], mediaType.ToLowerInvariant());
        }
        catch (Exception e) when (e is HttpRequestException or IOException
            || (e is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }
    }

    public void Dispose() => _cache.Dispose();
}
