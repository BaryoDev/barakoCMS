using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.ConnectorDeliveries.List;

/// <summary>
/// What Request actions sent through connectors, newest first.
/// </summary>
/// <remarks>
/// The rows are <see cref="WebhookDelivery"/> documents with a connector on them, so the webhook
/// delivery list's two gates apply unchanged: <see cref="SystemCapabilities.ViewWorkflowRuns"/> reads
/// a row, because a delivery is a run's action seen from the wire, and
/// <see cref="SystemCapabilities.ViewWebhookResponseBodies"/> reads the response body on it. Not the
/// connector capabilities: nothing here is a connector's configuration, and the person asking "did
/// it fire" is the one reading runs.
///
/// The request headers answer to the narrower capability too, which a webhook row's do not need.
/// A webhook's headers are all written by this application. A request's are written by an
/// operator, and reading them where they are configured needs <c>manage_requests</c>.
/// </remarks>
internal sealed class Endpoint(
    IQuerySession session,
    IPermissionResolver permissionResolver) : Endpoint<ListConnectorDeliveriesRequest, PaginatedResponse<ConnectorDeliveryResponse>>
{
    public override void Configure()
    {
        Get("/api/connector-deliveries");
        Definition.RequireCapability(SystemCapabilities.ViewWorkflowRuns, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(ListConnectorDeliveriesRequest req, CancellationToken ct)
    {
        var query = session.Query<WebhookDelivery>().Where(d => d.ConnectorId != null);

        if (!string.IsNullOrWhiteSpace(req.Connector))
        {
            var connector = req.Connector.Trim();
            query = query.Where(d => d.ConnectorSlug == connector);
        }

        if (!string.IsNullOrWhiteSpace(req.RequestSlug))
        {
            var request = req.RequestSlug.Trim();
            query = query.Where(d => d.RequestSlug == request);
        }

        if (req.WorkflowId is { } workflowId)
        {
            query = query.Where(d => d.WorkflowId == workflowId);
        }

        if (req.RunId is { } runId)
        {
            query = query.Where(d => d.RunId == runId);
        }

        query = req.Status?.Trim().ToLowerInvariant() switch
        {
            "2xx" => query.Where(d => d.ResponseStatus >= 200 && d.ResponseStatus < 300),
            "3xx" => query.Where(d => d.ResponseStatus >= 300 && d.ResponseStatus < 400),
            "4xx" => query.Where(d => d.ResponseStatus >= 400 && d.ResponseStatus < 500),
            "5xx" => query.Where(d => d.ResponseStatus >= 500 && d.ResponseStatus < 600),
            "failed" => query.Where(d => d.ResponseStatus == null),
            _ => query,
        };

        var page = await query.OrderByDescending(d => d.CreatedAt).ToPagedResponseAsync(req, ct);

        // A row can hold what a provider said in a 401, so no cache keeps a page.
        HttpContext.Response.Headers.CacheControl = "no-store";

        var canReadResponseBody = Guid.TryParse(User.FindFirst("UserId")?.Value, out var userId)
            && await permissionResolver.HasCapabilityAsync(userId, SystemCapabilities.ViewWebhookResponseBodies, ct);

        await Send.ResponseAsync(new PaginatedResponse<ConnectorDeliveryResponse>
        {
            Items = page.Items.Select(d => ConnectorDeliveryResponse.From(d, canReadResponseBody)).ToList(),
            Page = page.Page,
            PageSize = page.PageSize,
            TotalItems = page.TotalItems,
        }, cancellation: ct);
    }
}
