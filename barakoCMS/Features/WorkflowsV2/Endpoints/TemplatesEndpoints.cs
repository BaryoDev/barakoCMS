using barakoCMS.Features.WorkflowsV2.Services;
using FastEndpoints;

namespace barakoCMS.Features.WorkflowsV2.Endpoints;

// List templates
public class ListTemplatesResponse
{
    public List<TemplateSummary> Templates { get; set; } = new();
}

public class TemplateSummary
{
    public string Name { get; set; } = "";
    public string Subject { get; set; } = "";
    public List<string> Variables { get; set; } = new();
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class ListTemplatesEndpoint : EndpointWithoutRequest<ListTemplatesResponse>
{
    private readonly IEmailTemplateService _templateService;

    public ListTemplatesEndpoint(IEmailTemplateService templateService)
    {
        _templateService = templateService;
    }

    public override void Configure()
    {
        Get("/api/workflows/v2/templates");
        Roles("Admin", "WorkflowAdmin", "WorkflowViewer");
        Description(b => b.WithTags("WorkflowsV2"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var templates = await _templateService.ListTemplatesAsync();

        await SendAsync(new ListTemplatesResponse
        {
            Templates = templates.Select(t => new TemplateSummary
            {
                Name = t.Name,
                Subject = t.Subject,
                Variables = t.Variables,
                CreatedAt = t.CreatedAt,
                UpdatedAt = t.UpdatedAt
            }).ToList()
        }, cancellation: ct);
    }
}

// Get template
public class GetTemplateRequest
{
    public string Name { get; set; } = "";
}

public class GetTemplateResponse
{
    public string Name { get; set; } = "";
    public string Subject { get; set; } = "";
    public string HtmlBody { get; set; } = "";
    public string? TextBody { get; set; }
    public List<string> Variables { get; set; } = new();
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class GetTemplateEndpoint : Endpoint<GetTemplateRequest, GetTemplateResponse>
{
    private readonly IEmailTemplateService _templateService;

    public GetTemplateEndpoint(IEmailTemplateService templateService)
    {
        _templateService = templateService;
    }

    public override void Configure()
    {
        Get("/api/workflows/v2/templates/{Name}");
        Roles("Admin", "WorkflowAdmin", "WorkflowViewer");
        Description(b => b.WithTags("WorkflowsV2"));
    }

    public override async Task HandleAsync(GetTemplateRequest req, CancellationToken ct)
    {
        var template = await _templateService.GetTemplateAsync(req.Name);

        if (template == null)
        {
            await SendNotFoundAsync(ct);
            return;
        }

        await SendAsync(new GetTemplateResponse
        {
            Name = template.Name,
            Subject = template.Subject,
            HtmlBody = template.HtmlBody,
            TextBody = template.TextBody,
            Variables = template.Variables,
            CreatedAt = template.CreatedAt,
            UpdatedAt = template.UpdatedAt
        }, cancellation: ct);
    }
}

// Create/Update template
public class SaveTemplateRequest
{
    public string Name { get; set; } = "";
    public string Subject { get; set; } = "";
    public string HtmlBody { get; set; } = "";
    public string? TextBody { get; set; }
    public List<string> Variables { get; set; } = new();
}

public class SaveTemplateResponse
{
    public string Name { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
}

public class SaveTemplateEndpoint : Endpoint<SaveTemplateRequest, SaveTemplateResponse>
{
    private readonly IEmailTemplateService _templateService;

    public SaveTemplateEndpoint(IEmailTemplateService templateService)
    {
        _templateService = templateService;
    }

    public override void Configure()
    {
        Put("/api/workflows/v2/templates/{Name}");
        Roles("Admin", "WorkflowAdmin");
        Description(b => b.WithTags("WorkflowsV2"));
    }

    public override async Task HandleAsync(SaveTemplateRequest req, CancellationToken ct)
    {
        var template = new EmailTemplate
        {
            Name = req.Name,
            Subject = req.Subject,
            HtmlBody = req.HtmlBody,
            TextBody = req.TextBody,
            Variables = req.Variables,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await _templateService.SaveTemplateAsync(template);

        await SendAsync(new SaveTemplateResponse
        {
            Name = template.Name,
            UpdatedAt = template.UpdatedAt
        }, cancellation: ct);
    }
}

// Delete template
public class DeleteTemplateRequest
{
    public string Name { get; set; } = "";
}

public class DeleteTemplateEndpoint : Endpoint<DeleteTemplateRequest, object>
{
    private readonly IEmailTemplateService _templateService;

    public DeleteTemplateEndpoint(IEmailTemplateService templateService)
    {
        _templateService = templateService;
    }

    public override void Configure()
    {
        Delete("/api/workflows/v2/templates/{Name}");
        Roles("Admin", "WorkflowAdmin");
        Description(b => b.WithTags("WorkflowsV2"));
    }

    public override async Task HandleAsync(DeleteTemplateRequest req, CancellationToken ct)
    {
        await _templateService.DeleteTemplateAsync(req.Name);

        await SendNoContentAsync(ct);
    }
}

// Preview template
public class PreviewTemplateRequest
{
    public string? TemplateName { get; set; }
    public string? TemplateContent { get; set; }
    public Dictionary<string, object> Variables { get; set; } = new();
}

public class PreviewTemplateResponse
{
    public string RenderedContent { get; set; } = "";
}

public class PreviewTemplateEndpoint : Endpoint<PreviewTemplateRequest, PreviewTemplateResponse>
{
    private readonly IEmailTemplateService _templateService;

    public PreviewTemplateEndpoint(IEmailTemplateService templateService)
    {
        _templateService = templateService;
    }

    public override void Configure()
    {
        Post("/api/workflows/v2/templates/preview");
        Roles("Admin", "WorkflowAdmin");
        Description(b => b.WithTags("WorkflowsV2"));
    }

    public override async Task HandleAsync(PreviewTemplateRequest req, CancellationToken ct)
    {
        string content;

        if (!string.IsNullOrEmpty(req.TemplateName))
        {
            var template = await _templateService.GetTemplateAsync(req.TemplateName);
            if (template == null)
            {
                await SendNotFoundAsync(ct);
                return;
            }
            content = template.HtmlBody;
        }
        else if (!string.IsNullOrEmpty(req.TemplateContent))
        {
            content = req.TemplateContent;
        }
        else
        {
            AddError("Either templateName or templateContent is required");
            await SendErrorsAsync(400, ct);
            return;
        }

        var rendered = _templateService.RenderTemplate(content, req.Variables);

        await SendAsync(new PreviewTemplateResponse
        {
            RenderedContent = rendered
        }, cancellation: ct);
    }
}
