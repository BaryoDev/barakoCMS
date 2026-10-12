using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BarakoCMS.Diagnostics.Redaction;
using Marten;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BarakoCMS.Diagnostics.Forwarding;

/// <summary>The typed client the sender posts through, so a test can replace its handler.</summary>
internal sealed class SentryForwardClient(HttpClient http)
{
    public HttpClient Http { get; } = http;
}

/// <summary>
/// Sends queued reports to the receiver, one envelope each, redacted on the way out by the same
/// rules as the incidents view. A failure is logged and dropped: there is no retry, because a
/// retry queue is a second place memory can grow.
/// </summary>
internal sealed class ClientErrorForwardingService(
    ClientErrorForwarder forwarder,
    IServiceScopeFactory scopes,
    IOptions<ForwardingOptions> options,
    ILogger<ClientErrorForwardingService> logger) : BackgroundService
{
    private static readonly TimeSpan SensitiveNamesFor = TimeSpan.FromMinutes(1);

    private DiagnosticRedactor _redactor = new();
    private DateTimeOffset _redactorBuiltAt = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (forwarder.Reader is not { } reader || forwarder.Dsn is not { } dsn)
        {
            return;
        }

        await foreach (var error in reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await SendAsync(dsn, error, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // The exception type only: the message of an HTTP failure can carry the URL.
                logger.LogWarning("Forwarding a client error failed with {ExceptionType}; it was dropped.", ex.GetType().Name);
            }
        }
    }

    private async Task SendAsync(SentryDsn dsn, ForwardedError error, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var redactor = await RedactorAsync(scope.ServiceProvider, ct);
        var body = Envelope(dsn, error, options.Value.Environment, redactor);

        using var request = new HttpRequestMessage(HttpMethod.Post, dsn.EnvelopeEndpoint)
        {
            Content = new StringContent(body, Encoding.UTF8),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-sentry-envelope");
        request.Headers.TryAddWithoutValidation("X-Sentry-Auth",
            $"Sentry sentry_version=7, sentry_client=barakocms-diagnostics/1.0, sentry_key={dsn.PublicKey}");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.Value.TimeoutSeconds, 1, 60)));
        var client = scope.ServiceProvider.GetRequiredService<SentryForwardClient>();
        using var response = await client.Http.SendAsync(request, timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("The error receiver answered {Status}; the report was dropped.", (int)response.StatusCode);
        }
    }

    private async Task<DiagnosticRedactor> RedactorAsync(IServiceProvider services, CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow - _redactorBuiltAt < SensitiveNamesFor)
        {
            return _redactor;
        }
        await using var session = services.GetRequiredService<IDocumentStore>().QuerySession();
        _redactor = new DiagnosticRedactor(await SensitiveFieldNames.LoadAsync(session, allTenants: true, ct));
        _redactorBuiltAt = DateTimeOffset.UtcNow;
        return _redactor;
    }

    /// <summary>
    /// The envelope sent for one report. Every field that leaves the process is set here, and the
    /// module README lists the same set: anything not named here is not sent.
    /// </summary>
    internal static string Envelope(SentryDsn dsn, ForwardedError e, string? environment, DiagnosticRedactor redactor)
    {
        var eventId = Guid.NewGuid().ToString("N");
        var tags = new Dictionary<string, string> { ["kind"] = e.Kind };
        if (e.Tenant is not null)
        {
            tags["tenant"] = e.Tenant;
        }
        if (e.Status is { } status)
        {
            tags["status"] = status.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var evt = new Dictionary<string, object?>
        {
            ["event_id"] = eventId,
            ["timestamp"] = e.At.ToUnixTimeMilliseconds() / 1000.0,
            ["platform"] = "javascript",
            ["level"] = e.Severity == "warning" ? "warning" : "error",
            ["logger"] = "barakocms.client-errors",
            ["message"] = new { formatted = redactor.Apply(e.Message) },
            ["culprit"] = redactor.Apply(e.Source),
            ["fingerprint"] = new[] { e.Fingerprint },
            ["tags"] = tags,
        };
        if (e.AppVersion is not null)
        {
            evt["release"] = e.AppVersion;
        }
        if (!string.IsNullOrWhiteSpace(environment))
        {
            evt["environment"] = environment;
        }
        if (e.Url is not null)
        {
            evt["request"] = new { url = redactor.Apply(e.Url) };
        }

        var header = JsonSerializer.Serialize(new { event_id = eventId, sent_at = DateTimeOffset.UtcNow.ToString("O"), dsn = dsn.Public });
        var item = JsonSerializer.Serialize(new { type = "event", content_type = "application/json" });
        return $"{header}\n{item}\n{JsonSerializer.Serialize(evt)}\n";
    }
}
