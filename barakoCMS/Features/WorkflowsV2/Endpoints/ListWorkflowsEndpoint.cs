using barakoCMS.Features.WorkflowsV2.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.WorkflowsV2.Endpoints;

public class ListWorkflowsRequest
{
    [QueryParam]
    public int Page { get; set; } = 1;

    [QueryParam]
    public int PageSize { get; set; } = 20;

    [QueryParam]
    public string? ContentType { get; set; }

    [QueryParam]
    public string? TriggerEvent { get; set; }

    [QueryParam]
    public bool? Enabled { get; set; }
}

public class WorkflowSummary
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string TriggerContentType { get; set; } = "";
    public string TriggerEvent { get; set; } = "";
    public int ActionCount { get; set; }
    public int Priority { get; set; }
    public bool Enabled { get; set; }
    public int Version { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class ListWorkflowsResponse
{
    public List<WorkflowSummary> Workflows { get; set; } = new();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalPages { get; set; }
}

public class ListWorkflowsEndpoint : Endpoint<ListWorkflowsRequest, ListWorkflowsResponse>
{
    private readonly IDocumentSession _session;

    public ListWorkflowsEndpoint(IDocumentSession session)
    {
        _session = session;
    }

    public override void Configure()
    {
        Get("/api/workflows/v2");
        Roles("Admin", "WorkflowAdmin", "WorkflowViewer");
        Description(b => b.WithTags("WorkflowsV2"));
    }

    public override async Task HandleAsync(ListWorkflowsRequest req, CancellationToken ct)
    {
        IQueryable<WorkflowDefinitionV2> query = _session.Query<WorkflowDefinitionV2>();

        if (!string.IsNullOrEmpty(req.ContentType))
        {
            query = query.Where(w => w.Trigger.ContentType == req.ContentType);
        }

        if (!string.IsNullOrEmpty(req.TriggerEvent))
        {
            query = query.Where(w => w.Trigger.Event == req.TriggerEvent);
        }

        if (req.Enabled.HasValue)
        {
            query = query.Where(w => w.Enabled == req.Enabled.Value);
        }

        var totalCount = await query.CountAsync(ct);

        var workflows = await query
            .OrderByDescending(w => w.UpdatedAt)
            .Skip((req.Page - 1) * req.PageSize)
            .Take(req.PageSize)
            .ToListAsync(ct);

        var summaries = workflows.Select(w => new WorkflowSummary
        {
            Id = w.Id,
            Name = w.Name,
            Description = w.Description,
            TriggerContentType = w.Trigger.ContentType,
            TriggerEvent = w.Trigger.Event,
            ActionCount = w.Actions.Count,
            Priority = w.Priority,
            Enabled = w.Enabled,
            Version = w.Version,
            UpdatedAt = w.UpdatedAt
        }).ToList();

        await SendAsync(new ListWorkflowsResponse
        {
            Workflows = summaries,
            TotalCount = totalCount,
            Page = req.Page,
            PageSize = req.PageSize,
            TotalPages = (int)Math.Ceiling((double)totalCount / req.PageSize)
        }, cancellation: ct);
    }
}
