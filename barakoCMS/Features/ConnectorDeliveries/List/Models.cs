using barakoCMS.Models;

namespace barakoCMS.Features.ConnectorDeliveries.List;

internal sealed class ListConnectorDeliveriesRequest : ListRequest
{
    internal static readonly string[] StatusClasses = ["2xx", "3xx", "4xx", "5xx", "failed"];

    /// <summary>A connector's slug.</summary>
    public string? Connector { get; set; }

    /// <summary>A request definition's slug.</summary>
    public string? RequestSlug { get; set; }

    public Guid? WorkflowId { get; set; }

    public Guid? RunId { get; set; }

    /// <summary>A status class: <c>2xx</c>, <c>3xx</c>, <c>4xx</c>, <c>5xx</c>, or <c>failed</c> for no response at all.</summary>
    public string? Status { get; set; }
}

internal sealed class ConnectorDeliveryResponse
{
    public Guid Id { get; init; }
    public Guid WorkflowId { get; init; }
    public Guid? RunId { get; init; }
    public Guid? ConnectorId { get; init; }
    public string? ConnectorSlug { get; init; }
    public string? RequestSlug { get; init; }
    public string? Method { get; init; }
    public string Url { get; init; } = string.Empty;
    public string Event { get; init; } = string.Empty;
    public Dictionary<string, string> RequestHeaders { get; init; } = new();
    public int RequestsSent { get; init; }
    public int? ResponseStatus { get; init; }
    public string? ResponseBody { get; init; }
    public DateTimeOffset? ResponseBodyClearedAt { get; init; }
    public long DurationMs { get; init; }
    public string? Error { get; init; }
    public int Attempt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// <paramref name="canReadResponseBody"/> is resolved once for the request, the way the webhook
    /// delivery list does it.
    /// </summary>
    public static ConnectorDeliveryResponse From(WebhookDelivery d, bool canReadResponseBody) => new()
    {
        Id = d.Id,
        WorkflowId = d.WorkflowId,
        RunId = d.RunId,
        ConnectorId = d.ConnectorId,
        ConnectorSlug = d.ConnectorSlug,
        RequestSlug = d.RequestSlug,
        Method = d.Method,
        Url = d.Url,
        Event = d.Event,
        RequestHeaders = d.RequestHeaders,
        RequestsSent = d.RequestsSent ?? 0,
        ResponseStatus = d.ResponseStatus,
        ResponseBody = canReadResponseBody ? d.ResponseBody : null,
        ResponseBodyClearedAt = d.ResponseBodyClearedAt,
        DurationMs = d.DurationMs,
        Error = d.Error,
        Attempt = d.Attempt,
        CreatedAt = d.CreatedAt,
    };
}
