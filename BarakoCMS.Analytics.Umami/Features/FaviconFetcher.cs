using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Caching.Memory;

namespace BarakoCMS.Analytics.Umami.Features;

public sealed record Favicon(byte[] Bytes, string ContentType);

/// <summary>
/// Fetches a referrer's favicon on the server so the console never contacts a third party itself.
/// </summary>
/// <remarks>
/// It goes through the named "ExternalApi" client, whose connect callback refuses private addresses
/// and which follows no redirects, so a 3xx is a miss. The body is read with a bound rather than
/// trusting Content-Length. Answers are cached in a cache with a byte limit, charged at each entry's
/// real footprint, so a stream of distinct domains cannot grow memory without bound. A definite
/// answer (an icon, a 404, a wrong type) is kept for a day. A failure that may pass (a timeout, a 5xx,
/// a refused or broken connection) is kept for five minutes. Concurrent cold requests for one host
/// share a single fetch, and the number of fetches in flight is capped. SVG is not on the allowlist
/// because it can carry script.
/// </remarks>
public sealed class FaviconFetcher(
    IHttpClientFactory httpClientFactory,
    long cacheBytes = FaviconFetcher.DefaultCacheBytes,
    TimeProvider? time = null) : IDisposable
{
    public const int MaxBytes = 64 * 1024;
    public const long DefaultCacheBytes = 8 * 1024 * 1024;

    // What an entry costs beyond its bytes: the Entry, the cache bookkeeping and the key.
    private const int EntryOverhead = 256;
    private const int MaxConcurrentFetches = 16;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(1);
    public static readonly TimeSpan TransientLifetime = TimeSpan.FromMinutes(5);

    private static readonly HashSet<string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/x-icon", "image/vnd.microsoft.icon", "image/png", "image/gif", "image/jpeg", "image/webp",
    };

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = cacheBytes });
    private readonly ConcurrentDictionary<string, Lazy<Task<Favicon?>>> _inFlight = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(MaxConcurrentFetches);

    private sealed record Entry(Favicon? Icon, DateTimeOffset Until);

    private sealed record Outcome(Favicon? Icon, bool Transient);

    /// <summary>How many hosts, hits and misses, are cached right now.</summary>
    public int CachedEntries => _cache.Count;

    /// <param name="host">A hostname that passed <see cref="FaviconHostname.TryNormalize"/>.</param>
    public async Task<Favicon?> GetAsync(string host, CancellationToken ct)
    {
        if (_cache.TryGetValue(host, out Entry? cached) && cached is not null && cached.Until > _time.GetUtcNow())
        {
            return cached.Icon;
        }

        // The shared fetch runs on its own token, so one caller going away does not fail the rest.
        var load = _inFlight.GetOrAdd(host, h => new Lazy<Task<Favicon?>>(() => LoadAsync(h)));
        return await load.Value.WaitAsync(ct);
    }

    private async Task<Favicon?> LoadAsync(string host)
    {
        try
        {
            await _gate.WaitAsync();
            Outcome outcome;
            try
            {
                outcome = await FetchAsync(host);
            }
            finally
            {
                _gate.Release();
            }

            var lifetime = outcome.Transient ? TransientLifetime : Lifetime;
            _cache.Set(host, new Entry(outcome.Icon, _time.GetUtcNow() + lifetime), new MemoryCacheEntryOptions
            {
                Size = (outcome.Icon?.Bytes.Length ?? 0) + host.Length * 2 + EntryOverhead,
                AbsoluteExpirationRelativeToNow = lifetime,
            });
            return outcome.Icon;
        }
        finally
        {
            _inFlight.TryRemove(host, out _);
        }
    }

    private async Task<Outcome> FetchAsync(string host)
    {
        using var timeout = new CancellationTokenSource(Timeout);

        try
        {
            var client = httpClientFactory.CreateClient("ExternalApi");
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{host}/favicon.ico");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                var status = (int)response.StatusCode;
                return new Outcome(null, Transient: status >= 500 || status is 408 or 429);
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is null || !ImageTypes.Contains(mediaType))
            {
                return new Outcome(null, false);
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

            return read is 0 or > MaxBytes
                ? new Outcome(null, false)
                : new Outcome(new Favicon(buffer[..read], mediaType.ToLowerInvariant()), false);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TimeoutException or OperationCanceledException)
        {
            return new Outcome(null, true);
        }
    }

    public void Dispose()
    {
        _cache.Dispose();
        _gate.Dispose();
    }
}
