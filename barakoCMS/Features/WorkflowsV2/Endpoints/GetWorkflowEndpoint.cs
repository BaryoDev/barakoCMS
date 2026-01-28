using barakoCMS.Features.WorkflowsV2.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.WorkflowsV2.Endpoints;

public class GetWorkflowRequest
{
    public Guid Id { get; set; }
}

public class GetWorkflowResponse
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public WorkflowTrigger Trigger { get; set; } = new();
    public List<WorkflowActionV2> Actions { get; set; } = new();
    public WorkflowErrorHandling ErrorHandling { get; set; } = new();
    public int Priority { get; set; }
    public bool Enabled { get; set; }
    public int Version { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Guid CreatedBy { get; set; }
    public Guid UpdatedBy { get; set; }
}

public class GetWorkflowEndpoint : Endpoint<GetWorkflowRequest, GetWorkflowResponse>
{
    private readonly IDocumentSession _session;

    public GetWorkflowEndpoint(IDocumentSession session)
    {
        _session = session;
    }

    public override void Configure()
    {
        Get("/api/workflows/v2/{Id}");
        Roles("Admin", "WorkflowAdmin", "WorkflowViewer");
        Description(b => b.WithTags("WorkflowsV2"));
    }

    public override async Task HandleAsync(GetWorkflowRequest req, CancellationToken ct)
    {
        var workflow = await _session.LoadAsync<WorkflowDefinitionV2>(req.Id, ct);

        if (workflow == null)
        {
            await SendNotFoundAsync(ct);
            return;
        }

        await SendAsync(new GetWorkflowResponse
        {
            Id = workflow.Id,
            Name = workflow.Name,
            Description = workflow.Description,
            Trigger = workflow.Trigger,
            Actions = workflow.Actions,
            ErrorHandling = workflow.ErrorHandling,
            Priority = workflow.Priority,
            Enabled = workflow.Enabled,
            Version = workflow.Version,
            CreatedAt = workflow.CreatedAt,
            UpdatedAt = workflow.UpdatedAt,
            CreatedBy = workflow.CreatedBy,
            UpdatedBy = workflow.UpdatedBy
        }, cancellation: ct);
    }
}
