using System.Net.Http.Json;
using System.Text.Json;

namespace barakoCMS.Infrastructure.Caching.Purge;

/// <summary>
/// Purges by surrogate key with <c>POST service/{service}/purge</c> and a body of
/// <c>{"surrogate_keys":[...]}</c>.
/// </summary>
internal sealed class FastlyPurger(HttpClient http, CdnPurgeTarget target, ILogger<FastlyPurger> logger)
    : CdnPurgerBase(http, target, logger)
{
    public const string DefaultBaseUrl = "https://api.fastly.com";

    // Fastly: "Up to 256 surrogate keys can be purged in one batch request."
    // https://www.fastly.com/documentation/reference/api/purging/
    public const int MaxKeysPerCall = 256;

    protected override string ProviderName => "Fastly";

    protected override int MaxPerCall => MaxKeysPerCall;

    protected override HttpRequestMessage BuildRequest(IReadOnlyList<string> batch)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, UriFor($"service/{Uri.EscapeDataString(Target.TargetId)}/purge"))
        {
            Content = JsonContent.Create(new { surrogate_keys = batch }),
        };
        request.Headers.TryAddWithoutValidation("Fastly-Key", Target.Token);
        return request;
    }

    protected override string? Describe(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        var parts = new[] { "msg", "detail" }
            .Select(name => body.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null)
            .Where(s => !string.IsNullOrWhiteSpace(s));
        return string.Join(" ", parts);
    }
}
