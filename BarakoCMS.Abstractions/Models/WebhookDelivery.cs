namespace barakoCMS.Models;

/// <summary>
/// One attempt to deliver a webhook, or to send a request through a connector: where it went, what
/// was sent, what came back.
/// </summary>
/// <remarks>
/// Written on success and on failure, because "did it fire?" is the first question every time and
/// the application log was the only place that could answer it. One row per attempt, so a retry is
/// a second row rather than an overwrite of the first.
///
/// The URL is stored redacted (scheme, host and port) the way the run error is, because a webhook
/// URL routinely carries a token in its path or query and this row is served over the API. The signature
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

    /// <summary>Scheme, host and port. Never the userinfo, the path or the query.</summary>
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

    /// <summary>
    /// The connector a Request action sent through. Null on a webhook row, which is how the two
    /// kinds are told apart: a row stored before connector sends were recorded has no such field
    /// and reads as a webhook row.
    /// </summary>
    /// <remarks>
    /// A connector row follows the rules above with three differences. <see cref="RequestHeaders"/>
    /// keeps the name of a header that carries a credential and replaces its value, and is read
    /// with the capability that reads <see cref="ResponseBody"/>. The values that went on the
    /// request as credentials are cut out of <see cref="ResponseBody"/> before it is stored.
    /// <see cref="Error"/> also says when a response arrived and the request's success rule was not
    /// met.
    /// </remarks>
    public Guid? ConnectorId { get; set; }

    /// <summary>The connector's slug when the request was sent.</summary>
    public string? ConnectorSlug { get; set; }

    /// <summary>The slug of the request definition that was sent.</summary>
    public string? RequestSlug { get; set; }

    /// <summary>The HTTP method of a connector row. A webhook is always a POST and leaves this null.</summary>
    public string? Method { get; set; }

    /// <summary>
    /// How many times a connector row's request went to the provider: 0 when it was refused before
    /// anything was sent, 2 when a 401 to a cached OAuth token was answered with a new token and one
    /// more send. The status and body are the last answer's. Null on a webhook row.
    /// </summary>
    public int? RequestsSent { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
