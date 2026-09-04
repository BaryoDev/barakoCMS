using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.WebhookDeliveries;

internal sealed class WebhookDeliveryResponse
{
    public Guid Id { get; init; }
    public Guid WorkflowId { get; init; }
    public Guid? RunId { get; init; }
    public string Url { get; init; } = string.Empty;
    public string Event { get; init; } = string.Empty;
    public Dictionary<string, string> RequestHeaders { get; init; } = new();
    public int? ResponseStatus { get; init; }
    public string? ResponseBody { get; init; }
    public long DurationMs { get; init; }
    public string? Error { get; init; }
    public int Attempt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }

    public static WebhookDeliveryResponse From(WebhookDelivery d) => new()
    {
        Id = d.Id,
        WorkflowId = d.WorkflowId,
        RunId = d.RunId,
        Url = d.Url,
        Event = d.Event,
        RequestHeaders = d.RequestHeaders,
        ResponseStatus = d.ResponseStatus,
        ResponseBody = d.ResponseBody,
        DurationMs = d.DurationMs,
        Error = d.Error,
        Attempt = d.Attempt,
        CreatedAt = d.CreatedAt,
    };
}

internal sealed class ListDeliveriesRequest : ListRequest
{
    public Guid? WorkflowId { get; set; }

    /// <summary>A status class: <c>2xx</c>, <c>3xx</c>, <c>4xx</c>, <c>5xx</c>, or <c>failed</c> for no response at all.</summary>
    public string? Status { get; set; }
}

/// <summary>
/// The delivery log, newest first.
/// </summary>
/// <remarks>
/// Gated on the capability that reads workflow runs rather than one of its own. A delivery row is
/// a run's action seen from the wire, and "did it fire?" is the same question as "did the run
/// succeed?" asked by the same person.
/// </remarks>
internal sealed class ListDeliveriesEndpoint : Endpoint<ListDeliveriesRequest, PaginatedResponse<WebhookDeliveryResponse>>
{
    private static readonly string[] StatusClasses = ["2xx", "3xx", "4xx", "5xx", "failed"];

    private readonly IQuerySession _session;

    public ListDeliveriesEndpoint(IQuerySession session) => _session = session;

    public override void Configure()
    {
        Get("/api/webhook-deliveries");
        Definition.RequireCapability(SystemCapabilities.ViewWorkflowRuns, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(ListDeliveriesRequest req, CancellationToken ct)
    {
        var query = _session.Query<WebhookDelivery>().AsQueryable();

        if (req.WorkflowId is { } workflowId)
        {
            query = query.Where(d => d.WorkflowId == workflowId);
        }

        if (!string.IsNullOrWhiteSpace(req.Status))
        {
            var filtered = ByStatusClass(query, req.Status);
            if (filtered is null)
            {
                // Refused rather than ignored, the way the run list does it. A filter that is
                // silently dropped returns more rows than the caller asked for, and they cannot
                // tell that from "no matches".
                ThrowError($"Status must be one of: {string.Join(", ", StatusClasses)}.", 400);
                return;
            }

            query = filtered;
        }

        var page = await query.OrderByDescending(d => d.CreatedAt).ToPagedResponseAsync(req, ct);

        await Send.ResponseAsync(new PaginatedResponse<WebhookDeliveryResponse>
        {
            Items = page.Items.Select(WebhookDeliveryResponse.From).ToList(),
            Page = page.Page,
            PageSize = page.PageSize,
            TotalItems = page.TotalItems,
        }, cancellation: ct);
    }

    private static IQueryable<WebhookDelivery>? ByStatusClass(IQueryable<WebhookDelivery> query, string status) =>
        status.ToLowerInvariant() switch
        {
            "2xx" => query.Where(d => d.ResponseStatus >= 200 && d.ResponseStatus < 300),
            "3xx" => query.Where(d => d.ResponseStatus >= 300 && d.ResponseStatus < 400),
            "4xx" => query.Where(d => d.ResponseStatus >= 400 && d.ResponseStatus < 500),
            "5xx" => query.Where(d => d.ResponseStatus >= 500 && d.ResponseStatus < 600),
            "failed" => query.Where(d => d.ResponseStatus == null),
            _ => null,
        };
}
