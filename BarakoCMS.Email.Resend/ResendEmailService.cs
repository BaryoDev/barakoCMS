using System.Net.Http.Json;
using barakoCMS.Core.Interfaces;
using Microsoft.Extensions.Configuration;

namespace BarakoCMS.Email.Resend;

/// <summary>
/// Sends email through the Resend HTTP API (https://resend.com). Configured via:
/// <c>Resend:ApiKey</c> (or the RESEND_API_KEY env var) and <c>Resend:From</c>
/// (defaults to Resend's shared testing sender).
/// </summary>
public class ResendEmailService : IEmailService
{
    private const string Endpoint = "https://api.resend.com/emails";

    private readonly HttpClient _http;
    private readonly IConfiguration _config;

    public ResendEmailService(HttpClient http, IConfiguration config)
    {
        _http = http;
        _config = config;
    }

    public async Task SendEmailAsync(string to, string subject, string body, CancellationToken cancellationToken = default)
    {
        var apiKey = _config["Resend:ApiKey"] ?? Environment.GetEnvironmentVariable("RESEND_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Resend:ApiKey (or RESEND_API_KEY) is not configured.");

        var from = _config["Resend:From"] ?? "BaryoClub <onboarding@resend.dev>";

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
