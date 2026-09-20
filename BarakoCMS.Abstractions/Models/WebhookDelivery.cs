namespace barakoCMS.Models;

/// <summary>
/// One attempt to deliver a webhook: where it went, what was sent, what came back.
/// </summary>
/// <remarks>
/// Written on success and on failure, because "did it fire?" is the first question every time and
/// the application log was the only place that could answer it. One row per attempt, so a retry is
/// a second row rather than an overwrite of the first.
///
/// The URL is stored redacted (scheme, host, port, path) the way the run error is, because a webhook
/// URL routinely carries a token in its query and this row is served over the API. The signature
/// header is deliberately absent from <see cref="RequestHeaders"/>: a signature over a known body is
/// a hash of the secret, and a table of them is an offline guessing target.
///
/// <see cref="ResponseBody"/> is the one field here that answers to a narrower reader than the rest
/// of the row: a 401 from an OAuth provider frequently echoes the credential that was sent, which is
/// why <c>Features/WorkflowRuns/Endpoints.cs</c> and <c>Infrastructure/Connectors/ConnectorSender.cs</c>
/// carry no response body at all. This row keeps it, because "what did they say" is close to the
/// only way to debug a webhook a provider is rejecting, but reading it needs
/// <c>view_webhook_response_bodies</c> on top of the capability that reads the rest of the row, and
/// it does not outlive the debugging window: see <see cref="ResponseBodyClearedAt"/> and issue #607.
/// </remarks>
public class WebhookDelivery
{
    /// <summary>The most of a response body that is kept, in bytes.</summary>
    public const int ResponseBodyLimit = 4096;

    public Guid Id { get; set; }

    public Guid WorkflowId { get; set; }

    /// <summary>The run this delivery was part of, when the runner made it.</summary>
    public Guid? RunId { get; set; }

    /// <summary>Scheme, host, port and path. Never the userinfo or the query.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>The trigger event of the workflow that fired, for example <c>Published</c>.</summary>
    public string Event { get; set; } = string.Empty;

    /// <summary>What was sent, minus the signature.</summary>
    public Dictionary<string, string> RequestHeaders { get; set; } = new();

    /// <summary>Null when no response was received.</summary>
    public int? ResponseStatus { get; set; }

    /// <summary>The first <see cref="ResponseBodyLimit"/> bytes of what came back, as UTF-8.</summary>
    public string? ResponseBody { get; set; }

    /// <summary>
    /// When the retention sweep cleared <see cref="ResponseBody"/> for being past its window. Null on
    /// a row whose body was never captured in the first place (no response came back, or it has not
    /// been cleared yet), which is what tells "the debugging window closed" apart from "there was
    /// nothing to keep": both leave <see cref="ResponseBody"/> null, and only one of them sets this.
    /// </summary>
    public DateTimeOffset? ResponseBodyClearedAt { get; set; }

    public long DurationMs { get; set; }

    /// <summary>Why no response was received, when none was. Never a response body.</summary>
    public string? Error { get; set; }

    /// <summary>Which attempt at the action this was, counting from one.</summary>
    public int Attempt { get; set; } = 1;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
