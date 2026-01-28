using barakoCMS.Features.WorkflowsV2.Models;
using barakoCMS.Features.WorkflowsV2.Services;
using FastEndpoints;
using System.Security.Claims;

namespace barakoCMS.Features.WorkflowsV2.Endpoints;

// Export workflows
public class ExportWorkflowsRequest
{
    public List<Guid> WorkflowIds { get; set; } = new();
    public bool IncludeTemplates { get; set; } = true;
}

public class ExportWorkflowsEndpoint : Endpoint<ExportWorkflowsRequest, WorkflowExport>
{
    private readonly IWorkflowExportService _exportService;

    public ExportWorkflowsEndpoint(IWorkflowExportService exportService)
    {
        _exportService = exportService;
    }

    public override void Configure()
    {
        Post("/api/workflows/v2/export");
        Roles("Admin", "WorkflowAdmin");
        Description(b => b.WithTags("WorkflowsV2"));
    }

    public override async Task HandleAsync(ExportWorkflowsRequest req, CancellationToken ct)
    {
        if (req.WorkflowIds.Count == 0)
        {
            AddError("At least one workflow ID is required");
            await SendErrorsAsync(400, ct);
            return;
        }

        var export = await _exportService.ExportAsync(req.WorkflowIds, req.IncludeTemplates, ct);

        await SendAsync(export, cancellation: ct);
    }
}

// Import workflows
public class ImportWorkflowsRequest
{
    public WorkflowExport Package { get; set; } = new();
    public bool GenerateNewIds { get; set; } = true;
    public bool OverwriteExisting { get; set; } = false;
    public bool ImportTemplates { get; set; } = true;
    public string? NamePrefix { get; set; }
}

public class ImportWorkflowsEndpoint : Endpoint<ImportWorkflowsRequest, ImportResult>
{
    private readonly IWorkflowExportService _exportService;

    public ImportWorkflowsEndpoint(IWorkflowExportService exportService)
    {
        _exportService = exportService;
    }

    public override void Configure()
    {
        Post("/api/workflows/v2/import");
        Roles("Admin", "WorkflowAdmin");
        Description(b => b.WithTags("WorkflowsV2"));
    }

    public override async Task HandleAsync(ImportWorkflowsRequest req, CancellationToken ct)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? Guid.Empty.ToString());

        var options = new ImportOptions
        {
            GenerateNewIds = req.GenerateNewIds,
            OverwriteExisting = req.OverwriteExisting,
            ImportTemplates = req.ImportTemplates,
            NamePrefix = req.NamePrefix
        };

        var result = await _exportService.ImportAsync(req.Package, options, userId, ct);

        if (result.Success)
        {
            await SendAsync(result, cancellation: ct);
        }
        else
        {
            await SendAsync(result, 400, ct);
        }
    }
}

// Validate import
public class ValidateImportRequest
{
    public WorkflowExport Package { get; set; } = new();
}

public class ValidateImportResponse
{
    public bool Valid { get; set; }
    public List<string> Errors { get; set; } = new();
}

public class ValidateImportEndpoint : Endpoint<ValidateImportRequest, ValidateImportResponse>
{
    private readonly IWorkflowExportService _exportService;

    public ValidateImportEndpoint(IWorkflowExportService exportService)
    {
        _exportService = exportService;
    }

    public override void Configure()
    {
        Post("/api/workflows/v2/import/validate");
        Roles("Admin", "WorkflowAdmin");
        Description(b => b.WithTags("WorkflowsV2"));
    }

    public override async Task HandleAsync(ValidateImportRequest req, CancellationToken ct)
    {
        var errors = await _exportService.ValidateImportAsync(req.Package, ct);

        await SendAsync(new ValidateImportResponse
        {
            Valid = errors.Count == 0,
            Errors = errors
        }, cancellation: ct);
    }
}
