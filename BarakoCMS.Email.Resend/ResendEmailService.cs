using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Multitenancy;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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

    /// <summary>
    /// How long a send is remembered for attributing its bounce. Resend reports a bounce within
    /// minutes and a delay within days; an event for an older send is shown to global admins only.
    /// </summary>
    public static readonly TimeSpan SentEmailRetention = TimeSpan.FromDays(30);

    private readonly HttpClient http;
    private readonly IEmailSettingsProvider settings;
    private readonly TenantContext? tenant;
    private readonly IDocumentStore? store;
    private readonly ILogger<ResendEmailService>? logger;

    /// <summary>Sends without recording which tenant sent what, so bounces go to global admins only.</summary>
    public ResendEmailService(HttpClient http, IEmailSettingsProvider settings)
        : this(http, settings, null, null, null)
    {
    }

    [ActivatorUtilitiesConstructor]
    public ResendEmailService(
        HttpClient http,
        IEmailSettingsProvider settings,
        TenantContext? tenant,
        IDocumentStore? store,
        ILogger<ResendEmailService>? logger)
    {
        this.http = http;
        this.settings = settings;
        this.tenant = tenant;
        this.store = store;
        this.logger = logger;
    }

    /// <summary>Resend's shared testing sender, which works without a verified domain.</summary>
    private const string DefaultFrom = "BarakoCMS <onboarding@resend.dev>";

    public async Task SendEmailAsync(string to, string subject, string body, CancellationToken cancellationToken = default)
    {
        var resolved = await settings.GetAsync(cancellationToken);

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

        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Resend send failed ({(int)response.StatusCode}): {detail}");
        }

        await RecordSenderAsync(response, cancellationToken);
    }

    /// <summary>
    /// Keeps the tenant that sent this email against Resend's id for it, so the webhook can put a
    /// bounce back on that tenant. The email has already gone, so a failure here is logged rather
    /// than thrown: the cost is that its bounce shows to global admins only.
    /// </summary>
    private async Task RecordSenderAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (tenant is null || store is null) return;

        try
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var id = body.RootElement.TryGetProperty("id", out var value) ? value.GetString() : null;
            if (string.IsNullOrWhiteSpace(id))
            {
                logger?.LogWarning("Resend accepted an email without returning its id, so its bounces cannot be attributed to a tenant.");
                return;
            }

            await using var session = store.LightweightSession();
            session.Store(new SentEmail { Id = id, Tenant = tenant.Slug, At = DateTime.UtcNow });
            var cutoff = DateTime.UtcNow - SentEmailRetention;
            session.DeleteWhere<SentEmail>(s => s.At < cutoff);
            await session.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning("Could not record which tenant sent an email ({Exception}).", ex.GetType().Name);
        }
    }
}
