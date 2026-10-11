using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Core.Interfaces;
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
    private readonly IDocumentStore? store;
    private readonly ILogger<ResendEmailService>? logger;

    /// <summary>Sends without recording which tenant sent what, so bounces go to global admins only.</summary>
    public ResendEmailService(HttpClient http, IEmailSettingsProvider settings)
        : this(http, settings, null, null)
    {
    }

    [ActivatorUtilitiesConstructor]
    public ResendEmailService(
        HttpClient http,
        IEmailSettingsProvider settings,
        IDocumentStore? store,
        ILogger<ResendEmailService>? logger)
    {
        this.http = http;
        this.settings = settings;
        this.store = store;
        this.logger = logger;
    }

    /// <summary>
    /// The client's own timeout, which covers the whole request. Null when it has none.
    /// </summary>
    public TimeSpan? MaxNotSentDuration =>
        http.Timeout == System.Threading.Timeout.InfiniteTimeSpan ? null : http.Timeout;

    /// <summary>Resend's shared testing sender, which works without a verified domain.</summary>
    private const string DefaultFrom = "BarakoCMS <onboarding@resend.dev>";

    public Task SendEmailAsync(string to, string subject, string body, CancellationToken cancellationToken = default) =>
        SendEmailAsync(to, subject, body, Array.Empty<EmailAttachment>(), cancellationToken);

    public Task SendForTenantAsync(string tenant, string to, string subject, string body, CancellationToken cancellationToken = default) =>
        SendForTenantAsync(tenant, to, subject, body, Array.Empty<EmailAttachment>(), cancellationToken);

    public async Task SendEmailAsync(string to, string subject, string body, IReadOnlyList<EmailAttachment> attachments, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(to, subject, body, attachments, cancellationToken);
    }

    public async Task SendForTenantAsync(string tenant, string to, string subject, string body, IReadOnlyList<EmailAttachment> attachments, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(to, subject, body, attachments, cancellationToken);
        await RecordSenderAsync(tenant, response, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(
        string to, string subject, string body, IReadOnlyList<EmailAttachment> attachments, CancellationToken cancellationToken)
    {
        var resolved = await settings.GetAsync(cancellationToken);

        var apiKey = resolved.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException(
                "No Resend API key is set, in the admin under Settings, in Resend:ApiKey, or in RESEND_API_KEY.");

        var from = resolved.FromAddress ?? DefaultFrom;

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

        // Two shapes, so a message with no attachment is the request it always was.
        request.Content = attachments.Count == 0
            ? JsonContent.Create(new
            {
                from,
                to = new[] { to },
                subject,
                html = body,
            })
            : JsonContent.Create(new
            {
                from,
                to = new[] { to },
                subject,
                html = body,
                attachments = attachments.Select(a => new
                {
                    filename = a.FileName,
                    content = Convert.ToBase64String(a.Content),
                    content_type = a.ContentType,
                }).ToArray(),
            });

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex) when (NeverConnected(ex))
        {
            throw new EmailNotSentException($"Resend could not be reached ({ex.HttpRequestError}).", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            using (response)
            {
                var detail = await response.Content.ReadAsStringAsync(cancellationToken);
                var text = $"Resend send failed ({(int)response.StatusCode}): {detail}";
                throw Refused(response.StatusCode) ? new EmailNotSentException(text) : new InvalidOperationException(text);
            }
        }

        return response;
    }

    /// <summary>
    /// A name that did not resolve, a connection that was not made, or a TLS handshake that failed.
    /// The request had not been written, so Resend cannot have the message.
    /// </summary>
    /// <remarks>
    /// Not a connection that ended after the request went (<see cref="HttpRequestError.ResponseEnded"/>
    /// and the rest), and not a timeout, which surfaces as a cancellation: Resend may have accepted
    /// the message either way.
    /// </remarks>
    internal static bool NeverConnected(HttpRequestException ex) =>
        ex.HttpRequestError is HttpRequestError.NameResolutionError
            or HttpRequestError.ConnectionError
            or HttpRequestError.SecureConnectionError;

    /// <summary>
    /// A rate limit or an unavailable service: Resend answered and did not take the message. Other
    /// refusals are not sent either, but trying again inside the attempt does not change a bad key
    /// or a rejected sender.
    /// </summary>
    internal static bool Refused(HttpStatusCode status) =>
        status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable;

    /// <summary>
    /// Keeps the tenant that sent this email against Resend's id for it, so the webhook can put a
    /// bounce back on that tenant. The email has already gone, so a failure here is logged rather
    /// than thrown: the cost is that its bounce shows to global admins only.
    /// </summary>
    private async Task RecordSenderAsync(string tenant, HttpResponseMessage response, CancellationToken ct)
    {
        if (store is null || string.IsNullOrWhiteSpace(tenant)) return;

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
            session.Store(new SentEmail { Id = id, Tenant = tenant.Trim().ToLowerInvariant(), At = DateTime.UtcNow });
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
