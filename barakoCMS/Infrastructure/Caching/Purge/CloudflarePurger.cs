using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace barakoCMS.Infrastructure.Caching.Purge;

/// <summary>
/// Purges by tag with <c>POST zones/{zone}/purge_cache</c> and a body of <c>{"tags":[...]}</c>.
/// </summary>
internal sealed class CloudflarePurger(HttpClient http, CdnPurgeTarget target, ILogger<CloudflarePurger> logger)
    : CdnPurgerBase(http, target, logger)
{
    public const string DefaultBaseUrl = "https://api.cloudflare.com/client/v4";

    // Cloudflare: "You can purge up to 100 tags at a time."
    // https://developers.cloudflare.com/cache/how-to/purge-cache/purge-by-tags/
    public const int MaxTagsPerCall = 100;

    protected override string ProviderName => "Cloudflare";

    protected override int MaxPerCall => MaxTagsPerCall;

    protected override HttpRequestMessage BuildRequest(IReadOnlyList<string> batch)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, UriFor($"zones/{Uri.EscapeDataString(Target.TargetId)}/purge_cache"))
        {
            Content = JsonContent.Create(new { tags = batch }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Target.Token);
        return request;
    }

    protected override string? Describe(JsonElement body)
    {
        if (body.ValueKind == JsonValueKind.Object
            && body.TryGetProperty("errors", out var errors)
            && errors.ValueKind == JsonValueKind.Array)
        {
            var messages = errors.EnumerateArray()
                .Select(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var m) ? m.GetString() : null)
                .Where(m => !string.IsNullOrWhiteSpace(m));
            return string.Join("; ", messages);
        }
        return null;
    }
}
