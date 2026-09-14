using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BarakoCMS.Forms;

/// <summary>Checks a Cloudflare Turnstile token with Cloudflare.</summary>
internal interface ITurnstileVerifier
{
    Task<bool> VerifyAsync(string? token, string? remoteIp, CancellationToken ct);
}

internal sealed class TurnstileVerifier(HttpClient http, IOptions<FormsOptions> options, ILogger<TurnstileVerifier> logger)
    : ITurnstileVerifier
{
    public async Task<bool> VerifyAsync(string? token, string? remoteIp, CancellationToken ct)
    {
        var settings = options.Value.Turnstile;

        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(settings.SecretKey))
        {
            // Refusing is the safe direction: an operator who turned verification on expects it to
            // hold, and accepting everything would look exactly like it working.
            logger.LogError("Modules:Forms:Turnstile:Enabled is true but no SecretKey is set, so every submission is refused.");
            return false;
        }

        var form = new Dictionary<string, string> { ["secret"] = settings.SecretKey, ["response"] = token };
        if (!string.IsNullOrWhiteSpace(remoteIp))
        {
            form["remoteip"] = remoteIp;
        }

        try
        {
            using var response = await http.PostAsync(settings.VerifyUrl, new FormUrlEncodedContent(form), ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Turnstile verification answered {Status}.", (int)response.StatusCode);
                return false;
            }

            var result = await response.Content.ReadFromJsonAsync<SiteVerifyResult>(ct);
            return result?.Success == true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            logger.LogWarning(ex, "Turnstile verification could not be reached.");
            return false;
        }
    }

    private sealed class SiteVerifyResult
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }
    }
}
