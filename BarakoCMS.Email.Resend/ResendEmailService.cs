using System.Net.Http.Json;
using barakoCMS.Core.Interfaces;

namespace BarakoCMS.Email.Resend;

/// <summary>
/// Sends email through the Resend HTTP API (https://resend.com).
/// </summary>
/// <remarks>
/// Credentials come from <see cref="IEmailSettingsProvider"/> rather than straight from
/// <c>IConfiguration</c>, so an operator can set them in the admin without anybody editing the
/// deployment. The provider still reads <c>Resend:ApiKey</c> (or the RESEND_API_KEY environment
/// variable) and <c>Resend:From</c>, which is how a deployment with no database yet is seeded; what
/// an admin stored wins per field.
/// </remarks>
public class ResendEmailService : IEmailService
{
    private const string Endpoint = "https://api.resend.com/emails";

    /// <summary>Resend's shared testing sender, which works without a verified domain.</summary>
    private const string DefaultFrom = "BarakoCMS <onboarding@resend.dev>";

    private readonly HttpClient _http;
    private readonly IEmailSettingsProvider _settings;

    public ResendEmailService(HttpClient http, IEmailSettingsProvider settings)
    {
        _http = http;
        _settings = settings;
    }

    public async Task SendEmailAsync(string to, string subject, string body, CancellationToken cancellationToken = default)
    {
        var resolved = await _settings.GetAsync(cancellationToken);

        var apiKey = resolved.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException(
                "No Resend API key is set, in the admin under Settings, in Resend:ApiKey, or in RESEND_API_KEY.");

        var from = resolved.FromAddress ?? DefaultFrom;

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = JsonContent.Create(new
        {
            from,
            to = new[] { to },
            subject,
            html = body,
        });

        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Resend send failed ({(int)response.StatusCode}): {detail}");
        }
    }
}
