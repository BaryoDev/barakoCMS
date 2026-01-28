using barakoCMS.Features.WorkflowsV2.Models;
using barakoCMS.Features.WorkflowsV2.Services;
using FastEndpoints;
using System.Security.Claims;

namespace barakoCMS.Features.WorkflowsV2.Endpoints;

// List versions
public class ListVersionsRequest
{
    public Guid WorkflowId { get; set; }
}

public class VersionSummary
{
    public int Version { get; set; }
    public string ChangeDescription { get; set; } = "";
    public Guid CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsActive { get; set; }
}

public class ListVersionsResponse
{
    public Guid WorkflowId { get; set; }
    public List<VersionSummary> Versions { get; set; } = new();
}

public class ListVersionsEndpoint : Endpoint<ListVersionsRequest, ListVersionsResponse>
{
    private readonly IWorkflowVersionService _versionService;

    public ListVersionsEndpoint(IWorkflowVersionService versionService)
    {
        _versionService = versionService;
    }

    public override void Configure()
    {
        Get("/api/workflows/v2/{WorkflowId}/versions");
        Roles("Admin", "WorkflowAdmin", "WorkflowViewer");
        Description(b => b.WithTags("WorkflowsV2"));
    }

    public override async Task HandleAsync(ListVersionsRequest req, CancellationToken ct)
    {
        var versions = await _versionService.GetVersionsAsync(req.WorkflowId, ct);

        await SendAsync(new ListVersionsResponse
        {
            WorkflowId = req.WorkflowId,
            Versions = versions.Select(v => new VersionSummary
            {
                Version = v.Version,
                ChangeDescription = v.ChangeDescription,
                CreatedBy = v.CreatedBy,
                CreatedAt = v.CreatedAt,
                IsActive = v.IsActive
            }).ToList()
        }, cancellation: ct);
    }
}

// Get specific version
public class GetVersionRequest
{
    public Guid WorkflowId { get; set; }
    public int Version { get; set; }
}

public class GetVersionResponse
{
    public Guid WorkflowId { get; set; }
    public int Version { get; set; }
    public string ChangeDescription { get; set; } = "";
    public Guid CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsActive { get; set; }
    public WorkflowDefinitionV2? Definition { get; set; }
}

public class GetVersionEndpoint : Endpoint<GetVersionRequest, GetVersionResponse>
{
    private readonly IWorkflowVersionService _versionService;

    public GetVersionEndpoint(IWorkflowVersionService versionService)
    {
        _versionService = versionService;
    }

    public override void Configure()
    {
        Get("/api/workflows/v2/{WorkflowId}/versions/{Version}");
        Roles("Admin", "WorkflowAdmin", "WorkflowViewer");
        Description(b => b.WithTags("WorkflowsV2"));
    }

    public override async Task HandleAsync(GetVersionRequest req, CancellationToken ct)
    {
        var version = await _versionService.GetVersionAsync(req.WorkflowId, req.Version, ct);

        if (version == null)
        {
            await SendNotFoundAsync(ct);
            return;
        }

        await SendAsync(new GetVersionResponse
        {
            WorkflowId = version.WorkflowId,
            Version = version.Version,
            ChangeDescription = version.ChangeDescription,
            CreatedBy = version.CreatedBy,
            CreatedAt = version.CreatedAt,
            IsActive = version.IsActive,
            Definition = version.GetDefinition()
        }, cancellation: ct);
    }
}

// Rollback to version
public class RollbackVersionRequest
{
    public Guid WorkflowId { get; set; }
    public int Version { get; set; }
}

public class RollbackVersionResponse
{
    public Guid WorkflowId { get; set; }
    public int NewVersion { get; set; }
    public string Message { get; set; } = "";
}

public class RollbackVersionEndpoint : Endpoint<RollbackVersionRequest, RollbackVersionResponse>
{
    private readonly IWorkflowVersionService _versionService;

    public RollbackVersionEndpoint(IWorkflowVersionService versionService)
    {
        _versionService = versionService;
    }

    public override void Configure()
    {
        Post("/api/workflows/v2/{WorkflowId}/versions/{Version}/rollback");
        Roles("Admin", "WorkflowAdmin");
        Description(b => b.WithTags("WorkflowsV2"));
    }

    public override async Task HandleAsync(RollbackVersionRequest req, CancellationToken ct)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? Guid.Empty.ToString());

        try
        {
            var workflow = await _versionService.RollbackAsync(req.WorkflowId, req.Version, userId, ct);

            await SendAsync(new RollbackVersionResponse
            {
                WorkflowId = workflow.Id,
                NewVersion = workflow.Version,
                Message = $"Successfully rolled back to version {req.Version}"
            }, cancellation: ct);
        }
        catch (ArgumentException ex)
        {
            await SendNotFoundAsync(ct);
        }
        catch (InvalidOperationException ex)
        {
            AddError(ex.Message);
            await SendErrorsAsync(400, ct);
        }
    }
}

// Compare versions
public class CompareVersionsRequest
{
    public Guid WorkflowId { get; set; }

    [QueryParam]
    public int FromVersion { get; set; }

    [QueryParam]
    public int ToVersion { get; set; }
}

public class CompareVersionsEndpoint : Endpoint<CompareVersionsRequest, VersionDiff>
{
    private readonly IWorkflowVersionService _versionService;

    public CompareVersionsEndpoint(IWorkflowVersionService versionService)
    {
        _versionService = versionService;
    }

    public override void Configure()
    {
        Get("/api/workflows/v2/{WorkflowId}/versions/compare");
        Roles("Admin", "WorkflowAdmin", "WorkflowViewer");
        Description(b => b.WithTags("WorkflowsV2"));
    }

    public override async Task HandleAsync(CompareVersionsRequest req, CancellationToken ct)
    {
        try
        {
            var diff = await _versionService.CompareVersionsAsync(
                req.WorkflowId,
                req.FromVersion,
                req.ToVersion,
                ct);

            await SendAsync(diff, cancellation: ct);
        }
        catch (ArgumentException)
        {
            await SendNotFoundAsync(ct);
        }
    }
}
