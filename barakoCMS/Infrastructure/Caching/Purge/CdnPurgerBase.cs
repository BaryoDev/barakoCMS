using System.Net.Http.Headers;
using System.Text.Json;

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

    protected abstract string ProviderName { get; }

    protected abstract int MaxPerCall { get; }

    protected abstract HttpRequestMessage BuildRequest(IReadOnlyList<string> batch);

    protected abstract string? Describe(JsonElement body);

    protected CdnPurgeTarget Target => target;

    public async Task<CdnPurgeResult> PurgeTagsAsync(IReadOnlyList<string> tags, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tags);
        if (tags.Count == 0)
        {
            return CdnPurgeResult.Ok(null);
        }

        int? last = null;
        foreach (var batch in tags.Chunk(MaxPerCall))
        {
            using var request = BuildRequest(batch);

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                var reason = Scrub($"The {ProviderName} purge request failed: {ex.Message}");
                logger.LogWarning("CDN purge to {Provider} failed before an answer: {Reason}", ProviderName, reason);
                return new CdnPurgeResult(false, null, reason, Retryable: true);
            }

            using (response)
            {
                last = (int)response.StatusCode;
                if (response.IsSuccessStatusCode)
                {
                    continue;
                }

                var body = await response.Content.ReadAsStringAsync(ct);
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
        return clean.Length <= MaxErrorLength ? clean : clean[..MaxErrorLength];
    }

    protected Uri UriFor(string path) => new($"{target.BaseUrl.TrimEnd('/')}/{path}");
}
