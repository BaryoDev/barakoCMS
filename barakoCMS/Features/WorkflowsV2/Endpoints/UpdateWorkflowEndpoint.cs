using barakoCMS.Features.WorkflowsV2.Models;
using barakoCMS.Features.WorkflowsV2.Services;
using FastEndpoints;
using Marten;
using System.Security.Claims;

namespace barakoCMS.Features.WorkflowsV2.Endpoints;

public class UpdateWorkflowRequest
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public WorkflowTrigger Trigger { get; set; } = new();
    public List<WorkflowActionV2> Actions { get; set; } = new();
    public WorkflowErrorHandling? ErrorHandling { get; set; }
    public int Priority { get; set; } = 0;
    public bool Enabled { get; set; } = true;
    public string? ChangeDescription { get; set; }
}

public class UpdateWorkflowResponse
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public int Version { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class UpdateWorkflowEndpoint : Endpoint<UpdateWorkflowRequest, UpdateWorkflowResponse>
{
    private readonly IDocumentSession _session;
    private readonly IWorkflowVersionService _versionService;

    public UpdateWorkflowEndpoint(IDocumentSession session, IWorkflowVersionService versionService)
    {
        _session = session;
        _versionService = versionService;
    }

    public override void Configure()
    {
        Put("/api/workflows/v2/{Id}");
        Roles("Admin", "WorkflowAdmin");
        Description(b => b.WithTags("WorkflowsV2"));
    }

    public override async Task HandleAsync(UpdateWorkflowRequest req, CancellationToken ct)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? Guid.Empty.ToString());

        var workflow = await _session.LoadAsync<WorkflowDefinitionV2>(req.Id, ct);

        if (workflow == null)
        {
            await SendNotFoundAsync(ct);
            return;
        }

        workflow.Name = req.Name;
        workflow.Description = req.Description ?? "";
        workflow.Trigger = req.Trigger;
        workflow.Actions = req.Actions;
        workflow.ErrorHandling = req.ErrorHandling ?? new WorkflowErrorHandling();
        workflow.Priority = req.Priority;
        workflow.Enabled = req.Enabled;
        workflow.Version++;
        workflow.UpdatedAt = DateTime.UtcNow;
        workflow.UpdatedBy = userId;

        _session.Store(workflow);

        // Create new version
        await _versionService.CreateVersionAsync(
            workflow,
            userId,
            req.ChangeDescription ?? "Updated workflow",
            ct);

        await _session.SaveChangesAsync(ct);

        await SendAsync(new UpdateWorkflowResponse
        {
            Id = workflow.Id,
            Name = workflow.Name,
            Version = workflow.Version,
            UpdatedAt = workflow.UpdatedAt
        }, cancellation: ct);
    }
}
