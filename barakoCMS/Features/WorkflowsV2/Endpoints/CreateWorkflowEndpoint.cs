using barakoCMS.Features.WorkflowsV2.Models;
using barakoCMS.Features.WorkflowsV2.Services;
using FastEndpoints;
using Marten;
using System.Security.Claims;

namespace barakoCMS.Features.WorkflowsV2.Endpoints;

public class CreateWorkflowRequest
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public WorkflowTrigger Trigger { get; set; } = new();
    public List<WorkflowActionV2> Actions { get; set; } = new();
    public WorkflowErrorHandling? ErrorHandling { get; set; }
    public int Priority { get; set; } = 0;
    public bool Enabled { get; set; } = true;
}

public class CreateWorkflowResponse
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public int Version { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class CreateWorkflowEndpoint : Endpoint<CreateWorkflowRequest, CreateWorkflowResponse>
{
    private readonly IDocumentSession _session;
    private readonly IWorkflowVersionService _versionService;

    public CreateWorkflowEndpoint(IDocumentSession session, IWorkflowVersionService versionService)
    {
        _session = session;
        _versionService = versionService;
    }

    public override void Configure()
    {
        Post("/api/workflows/v2");
        Roles("Admin", "WorkflowAdmin");
        Description(b => b.WithTags("WorkflowsV2"));
    }

    public override async Task HandleAsync(CreateWorkflowRequest req, CancellationToken ct)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? Guid.Empty.ToString());

        var workflow = new WorkflowDefinitionV2
        {
            Id = Guid.NewGuid(),
            Name = req.Name,
            Description = req.Description ?? "",
            Trigger = req.Trigger,
            Actions = req.Actions,
            ErrorHandling = req.ErrorHandling ?? new WorkflowErrorHandling(),
            Priority = req.Priority,
            Enabled = req.Enabled,
            Version = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            CreatedBy = userId,
            UpdatedBy = userId
        };

        _session.Store(workflow);

        // Create initial version
        await _versionService.CreateVersionAsync(workflow, userId, "Initial creation", ct);

        await _session.SaveChangesAsync(ct);

        await SendAsync(new CreateWorkflowResponse
        {
            Id = workflow.Id,
            Name = workflow.Name,
            Version = workflow.Version,
            CreatedAt = workflow.CreatedAt
        }, 201, ct);
    }
}
