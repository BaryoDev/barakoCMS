using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Infrastructure.Connectors;

/// <summary>What a delivery row says about the run a send belonged to.</summary>
internal sealed record ConnectorDeliveryContext(
    string RequestSlug, Guid WorkflowId, Guid? RunId, string Event, int Attempt)
{
    /// <summary>
    /// Read from the parameters the runner puts on every action, the same ones
    /// <c>WebhookAction</c> reads. An action invoked some other way leaves them out, and the row is
    /// still written with what is known.
    /// </summary>
    public static ConnectorDeliveryContext From(string requestSlug, IReadOnlyDictionary<string, string> parameters)
    {
        Guid.TryParse(parameters.GetValueOrDefault("WorkflowId"), out var workflowId);

        return new ConnectorDeliveryContext(
            requestSlug,
            workflowId,
            Guid.TryParse(parameters.GetValueOrDefault("RunId"), out var runId) ? runId : null,
            parameters.GetValueOrDefault("TriggerEvent") ?? string.Empty,
            int.TryParse(parameters.GetValueOrDefault("Attempt"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var attempt)
            && attempt > 0
                ? attempt
                : 1);
    }
}

/// <summary>Sends a composed request through a connector and leaves a delivery row behind.</summary>
/// <remarks>
/// Internal, and separate from <see cref="IConnectorSender"/>, which is public and which a host may
/// already implement. A host's own sender records nothing, and the Request action then sends
/// through it as it did before.
/// </remarks>
internal interface IConnectorDeliverySender
{
    /// <summary>
    /// The same send as <see cref="IConnectorSender.SendAsync"/>, with one
    /// <see cref="WebhookDelivery"/> row written for it, whether it was sent, refused or failed.
    /// </summary>
    Task<ConnectorCallResult> SendAsync(
        Connector connector, ComposedRequest request, SuccessRule rule, string? successJsonPath,
        ConnectorDeliveryContext delivery, CancellationToken ct);
}

/// <summary>Where a connector delivery row is written.</summary>
internal interface IConnectorDeliveryLog
{
    Task WriteAsync(string tenantId, WebhookDelivery delivery, CancellationToken ct);
}

/// <remarks>
/// A session of its own for each row, opened on the tenant the send ran in. The scope's own session
/// is left alone: a write that fails there stays queued in it, and the runner's next save in the
/// same scope would fail with it.
/// </remarks>
internal sealed class ConnectorDeliveryLog(IDocumentStore store) : IConnectorDeliveryLog
{
    public async Task WriteAsync(string tenantId, WebhookDelivery delivery, CancellationToken ct)
    {
        await using var session = store.LightweightSession(tenantId);
        session.Store(delivery);
        await session.SaveChangesAsync(ct);
    }
}

/// <summary>What one send has shown so far of the row it will leave behind.</summary>
internal sealed class ConnectorDeliveryDraft(ComposedRequest composed)
{
    private readonly HashSet<string> _secrets = ConnectorDeliveryRedaction.BodySecrets(composed);

    public Dictionary<string, string> RequestHeaders { get; private set; } = new();

    public int RequestsSent { get; private set; }

    public string? ResponseBody { get; private set; }

    public IReadOnlyCollection<string> Secrets => _secrets;

    /// <summary>
    /// Called with the finished request, credentials attached, each time one is about to go out.
    /// The headers kept are the last request's. The credentials kept are every request's, since a
    /// provider can echo the first token in its answer to the second.
    /// </summary>
    public void Sending(HttpRequestMessage request)
    {
        RequestsSent++;
        RequestHeaders = ConnectorDeliveryRedaction.Headers(request, composed, _secrets);
    }

    /// <summary>Keeps the answer's body, cut and with the credentials taken out.</summary>
    public async Task ReadResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            ResponseBody = await ConnectorDeliveryRedaction.ReadBodyAsync(response.Content, _secrets, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // A body that cannot be read is a row without one, not a failed send.
            ResponseBody = null;
        }
    }

    public WebhookDelivery ToRow(Connector connector, ConnectorDeliveryContext context, ConnectorCallResult result) => new()
    {
        Id = Guid.NewGuid(),
        WorkflowId = context.WorkflowId,
        RunId = context.RunId,
        Event = context.Event,
        Attempt = context.Attempt,
        ConnectorId = connector.Id,
        ConnectorSlug = connector.Slug,
        RequestSlug = context.RequestSlug,
        Method = composed.Ok ? composed.Method : null,
        Url = WebhookAction.Redact(composed.Ok ? composed.Url : connector.BaseUrl),
        RequestHeaders = RequestHeaders,
        RequestsSent = RequestsSent,
        ResponseStatus = result.StatusCode,
        ResponseBody = ResponseBody,
        DurationMs = result.ElapsedMs,
        Error = result.Error is null ? null : ConnectorDeliveryRedaction.Scrub(result.Error, _secrets, result.Error.Length),
        CreatedAt = DateTimeOffset.UtcNow,
    };
}

/// <summary>What of a connector send may be written down.</summary>
/// <remarks>
/// The sender attaches credentials to the finished request, so the request as sent holds them and
/// the request as composed does not. That difference is the rule: a header value is kept only when
/// it is exactly what was composed, its name does not read as a credential, and it holds none of
/// the values that were replaced. Everything else keeps its name and loses its value.
///
/// The request body is not stored at all, as a webhook row does not store one.
///
/// The response body is cut to <see cref="WebhookDelivery.ResponseBodyLimit"/> the way a webhook
/// row's is, with one more step: every value replaced in the headers, and the value of every field
/// of the request body whose name reads as a credential, is also taken out of the response body,
/// because a provider answering 401 or 400 often quotes what it was sent. Only an exact copy is
/// found. A provider that echoes a credential encoded some other way is why the body still needs
/// <c>view_webhook_response_bodies</c> to read and is cleared by the retention sweep.
/// </remarks>
internal static class ConnectorDeliveryRedaction
{
    internal const string Marker = "[redacted]";

    private static readonly string[] CredentialHeaderParts = ["auth", "cookie", "signature"];

    /// <summary>Whether a header an operator wrote reads as carrying a credential.</summary>
    /// <remarks>
    /// The parameter classifier, asked twice: once with the name as written and once with the
    /// separators removed, since it knows <c>apikey</c> and <c>api_key</c> and a header is spelled
    /// <c>X-Api-Key</c>. The three words added here are header names that classifier has no reason
    /// to know.
    /// </remarks>
    internal static bool IsCredentialHeader(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;

        return IsCredentialField(name)
            || CredentialHeaderParts.Any(part => Compact(name).Contains(part, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The values an operator wrote into the request body under a name that reads as a credential,
    /// so an answer that quotes the body back does not put them in the row.
    /// </summary>
    internal static HashSet<string> BodySecrets(ComposedRequest composed)
    {
        var secrets = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(composed.Body)) return secrets;

        var contentType = composed.BodyContentType ?? "application/json";

        if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var json = JsonDocument.Parse(composed.Body);
                AddCredentialFields(json.RootElement, false, secrets);
            }
            catch (JsonException)
            {
                // The composer refuses a JSON body that does not parse, so this is a body nothing sent.
            }
        }
        else if (contentType.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var pair in composed.Body.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var split = pair.IndexOf('=');
                if (split <= 0 || !IsCredentialField(FormDecode(pair[..split]))) continue;

                AddSecret(secrets, pair[(split + 1)..]);
                AddSecret(secrets, FormDecode(pair[(split + 1)..]));
            }
        }

        return secrets;
    }

    private static bool IsCredentialField(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;

        return WebhookSigning.IsSensitiveParameterName(name)
            || WebhookSigning.IsSensitiveParameterName(Compact(name));
    }

    private static string Compact(string name) =>
        name.Replace("-", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal);

    private static string FormDecode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));

    private static void AddCredentialFields(JsonElement element, bool underCredential, ISet<string> secrets)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    AddCredentialFields(property.Value, underCredential || IsCredentialField(property.Name), secrets);
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    AddCredentialFields(item, underCredential, secrets);
                }

                break;

            case JsonValueKind.String when underCredential:
                // As a provider would quote it back from the parsed value, and as it stood in the
                // body, escapes and all.
                AddSecret(secrets, element.GetString());
                AddSecret(secrets, element.GetRawText().Trim('"'));
                break;
        }
    }

    /// <summary>
    /// The headers of a request as a row may hold them, adding every value left out to
    /// <paramref name="secrets"/>.
    /// </summary>
    internal static Dictionary<string, string> Headers(
        HttpRequestMessage request, ComposedRequest composed, ISet<string> secrets)
    {
        var written = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in composed.Headers) written[name] = value;

        var kept = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var plain = new List<(string Name, string Value)>();

        foreach (var (name, values) in request.Headers)
        {
            var all = values.ToList();

            var asComposed = all.Count == 1
                && written.TryGetValue(name, out var composedValue)
                && string.Equals(composedValue, all[0], StringComparison.Ordinal);

            if (asComposed && !IsCredentialHeader(name))
            {
                plain.Add((name, all[0]));
                continue;
            }

            foreach (var value in all) AddCredentialValue(secrets, value);
            kept[name] = Marker;
        }

        foreach (var (name, value) in plain)
        {
            kept[name] = secrets.Any(secret => value.Contains(secret, StringComparison.Ordinal)) ? Marker : value;
        }

        if (request.Content is not null)
        {
            foreach (var (name, values) in request.Content.Headers)
            {
                kept[name] = string.Join(", ", values);
            }
        }

        return kept;
    }

    /// <summary>
    /// A header's value, and when it is a scheme and a token, the token alone and both halves of a
    /// Basic pair, so an answer that quotes any of those forms is still caught.
    /// </summary>
    private static void AddCredentialValue(ISet<string> secrets, string value)
    {
        AddSecret(secrets, value);

        if (!AuthenticationHeaderValue.TryParse(value, out var parsed) || parsed.Parameter is not { Length: > 0 } parameter)
        {
            return;
        }

        AddSecret(secrets, parameter);

        if (!string.Equals(parsed.Scheme, "Basic", StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            var pair = Encoding.UTF8.GetString(Convert.FromBase64String(parameter));
            AddSecret(secrets, pair);

            var colon = pair.IndexOf(':');
            if (colon >= 0) AddSecret(secrets, pair[(colon + 1)..]);
        }
        catch (FormatException)
        {
            // Not base64, so there is no pair to take apart. The value itself is already held.
        }
    }

    private static void AddSecret(ISet<string> secrets, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) secrets.Add(value.Trim());
    }

    /// <summary>
    /// The first <see cref="WebhookDelivery.ResponseBodyLimit"/> bytes of a body with every secret
    /// in them replaced, or null when there is no body.
    /// </summary>
    /// <remarks>
    /// Reads past the limit by the length of the longest secret. One that begins before the cut
    /// and ends after it is then whole when it is looked for, and is replaced instead of being
    /// stored as its first half.
    /// </remarks>
    internal static async Task<string?> ReadBodyAsync(
        HttpContent content, IReadOnlyCollection<string> secrets, CancellationToken ct)
    {
        var limit = WebhookDelivery.ResponseBodyLimit;
        var longest = secrets.Count == 0 ? 0 : secrets.Max(secret => Encoding.UTF8.GetByteCount(secret));

        await using var stream = await content.ReadAsStreamAsync(ct);

        var buffer = new byte[limit + longest];
        var total = 0;

        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct);
            if (read == 0) break;
            total += read;
        }

        if (total == 0) return null;

        var text = Encoding.UTF8.GetString(buffer, 0, total);
        var cut = total <= limit ? text.Length : Math.Min(text.Length, Encoding.UTF8.GetCharCount(buffer, 0, limit));

        return Scrub(text, secrets, cut);
    }

    /// <summary>
    /// The first <paramref name="cut"/> characters of <paramref name="text"/>, with each secret that
    /// begins among them replaced. A replacement can be longer than what it replaces, so the result
    /// is cut again at <see cref="WebhookDelivery.ResponseBodyLimit"/> characters.
    /// </summary>
    internal static string Scrub(string text, IReadOnlyCollection<string> secrets, int cut)
    {
        cut = Math.Min(cut, text.Length);

        var longestFirst = secrets.Where(secret => secret.Length > 0).OrderByDescending(secret => secret.Length).ToList();
        if (longestFirst.Count == 0) return text[..cut];

        var scrubbed = new StringBuilder(cut);
        var at = 0;

        while (at < cut)
        {
            var found = longestFirst.FirstOrDefault(
                secret => string.CompareOrdinal(text, at, secret, 0, secret.Length) == 0);

            if (found is null)
            {
                scrubbed.Append(text[at]);
                at++;
                continue;
            }

            scrubbed.Append(Marker);
            at += found.Length;
        }

        if (scrubbed.Length > WebhookDelivery.ResponseBodyLimit) scrubbed.Length = WebhookDelivery.ResponseBodyLimit;

        return scrubbed.ToString();
    }
}
