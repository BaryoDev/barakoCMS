using System.Net.Http.Headers;
using System.Text.Json;
using barakoCMS.Infrastructure.Http;

namespace barakoCMS.Infrastructure.Caching.Purge;

/// <summary>
/// The batch loop, the failure classification and the token scrub the two providers share.
/// </summary>
/// <remarks>
/// The first batch that fails stops the rest and is reported. The token is sent only in a header and
/// is scrubbed from every string this class returns or logs, because a provider can echo a request
/// back in an error body.
/// </remarks>
internal abstract class CdnPurgerBase(HttpClient http, CdnPurgeTarget target, ILogger logger) : ICdnPurger
{
    private const int MaxErrorLength = 500;
    private const int MaxErrorBodyBytes = 8 * 1024;

    protected abstract string ProviderName { get; }

    protected abstract int MaxPerCall { get; }

    protected abstract HttpRequestMessage BuildRequest(IReadOnlyList<string> batch);

    protected abstract string? Describe(JsonElement body);

    protected CdnPurgeTarget Target => target;

    public async Task<CdnPurgeResult> PurgeTagsAsync(IReadOnlyList<string> tags, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tags);
        int? last = null;
        foreach (var batch in tags.Chunk(MaxPerCall))
        {
            using var request = BuildRequest(batch);

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException && !ct.IsCancellationRequested)
            {
                var reason = Scrub($"The {ProviderName} purge request failed: {ex.Message}");
                logger.LogWarning("CDN purge to {Provider} failed before an answer: {Reason}", ProviderName, reason);
                return new CdnPurgeResult(false, null, reason, Retryable: !IsBlockedAddress(ex));
            }

            using (response)
            {
                last = (int)response.StatusCode;
                if (response.IsSuccessStatusCode)
                {
                    continue;
                }

                var body = await ReadCappedAsync(response.Content, ct);
                var detail = DescribeBody(body);
                var error = Scrub($"{ProviderName} answered {last} to the purge. {detail}".TrimEnd());
                logger.LogWarning("CDN purge to {Provider} rejected with {Status}: {Error}", ProviderName, last, error);
                return new CdnPurgeResult(
                    false, last, error,
                    Retryable: last == 429 || last >= 500,
                    RetryAfter: RetryAfterOf(response.Headers.RetryAfter));
            }
        }

        return CdnPurgeResult.Ok(last);
    }

    private static bool IsBlockedAddress(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
        {
            if (ex is BlockedAddressException)
            {
                return true;
            }
        }
        return false;
    }

    private static async Task<string> ReadCappedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        var buffer = new byte[MaxErrorBodyBytes];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0)
            {
                break;
            }
            read += n;
        }
        return System.Text.Encoding.UTF8.GetString(buffer, 0, read);
    }

    private string DescribeBody(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return Describe(doc.RootElement) ?? string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private static TimeSpan? RetryAfterOf(RetryConditionHeaderValue? header)
    {
        if (header is null)
        {
            return null;
        }
        if (header.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }
        if (header.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }
        return null;
    }

    private string Scrub(string text)
    {
        var clean = target.Token.Length == 0
            ? text
            : text.Replace(target.Token, "[token]", StringComparison.Ordinal);
        clean = string.Create(clean.Length, clean, static (span, src) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = char.IsControl(src[i]) ? ' ' : src[i];
            }
        });
        return clean.Length <= MaxErrorLength ? clean : clean[..MaxErrorLength];
    }

    protected Uri UriFor(string path) => new($"{target.BaseUrl.TrimEnd('/')}/{path}");
}
