using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BarakoCMS.CdnPurge;

/// <summary>
/// Purges by tag with <c>POST zones/{zone}/purge_cache</c> and a body of <c>{"tags":[...]}</c>.
/// </summary>
/// <remarks>
/// Cloudflare takes at most 30 tags per call, so a longer list goes out in batches. The first batch
/// that fails stops the rest and is reported. The token is sent only as the bearer header and is
/// scrubbed from any text that is returned or logged.
/// </remarks>
public sealed class CloudflarePurger(
    HttpClient http,
    IOptions<CdnPurgeOptions> options,
    ILogger<CloudflarePurger> logger) : ICdnPurger
{
    public const int MaxTagsPerCall = 30;

    private readonly CdnPurgeOptions _options = options.Value;

    public async Task<CdnPurgeResult> PurgeTagsAsync(IReadOnlyList<string> tags, CancellationToken ct)
    {
        if (!_options.IsConfigured)
            return new CdnPurgeResult(false, null, "The CDN purge module has no ZoneId and ApiToken configured.");
        if (tags.Count == 0)
            return new CdnPurgeResult(false, null, "No tags to purge.");

        var uri = new Uri(
            new Uri(_options.BaseUrl.EndsWith('/') ? _options.BaseUrl : _options.BaseUrl + "/"),
            $"zones/{Uri.EscapeDataString(_options.ZoneId)}/purge_cache");

        var last = 0;
        foreach (var batch in tags.Chunk(MaxTagsPerCall))
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = JsonContent.Create(new { tags = batch }),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiToken);

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, ct);
            }
            catch (HttpRequestException ex)
            {
                var reason = Scrub("The purge request failed: " + ex.Message);
                logger.LogWarning("CDN purge failed before an answer: {Reason}", reason);
                return new CdnPurgeResult(false, null, reason, Retryable: true);
            }

            using (response)
            {
                last = (int)response.StatusCode;
                if (response.IsSuccessStatusCode)
                    continue;

                var body = await response.Content.ReadAsStringAsync(ct);
                var error = Scrub($"Cloudflare answered {last} to the purge. {Describe(body)}".TrimEnd());
                logger.LogWarning("CDN purge rejected with {Status}: {Error}", last, error);
                return new CdnPurgeResult(false, last, error, Retryable: last == 429 || last >= 500);
            }
        }

        return CdnPurgeResult.Ok(last);
    }

    private static string Describe(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
            {
                var messages = errors.EnumerateArray()
                    .Select(e => e.TryGetProperty("message", out var m) ? m.GetString() : null)
                    .Where(m => !string.IsNullOrWhiteSpace(m));
                return string.Join("; ", messages);
            }
        }
        catch (JsonException)
        {
        }
        return "";
    }

    private string Scrub(string text) =>
        _options.ApiToken.Length == 0 ? text : text.Replace(_options.ApiToken, "[token]", StringComparison.Ordinal);
}
