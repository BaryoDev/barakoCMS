using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FastEndpoints;

namespace barakoCMS.Features.Workflows.GetTemplateVariables;

/// <summary>
/// Request to get available template variables.
/// </summary>
internal class Request
{
    public string? ContentType { get; set; }
}

/// <summary>
/// Endpoint to get available template variables for a content type.
/// </summary>
internal class Endpoint(ITemplateVariableExtractor extractor) : Endpoint<Request, TemplateVariableCollection>
{
    public override void Configure()
    {
        Get("/api/workflows/variables");
        // Was AllowAnonymous "for testing". This reads a real stored document of the requested
        // content type to derive its data fields, so anonymously it disclosed both the field names
        // and, until the change in TemplateVariableExtractor, their stored values — bypassing both
        // the role restriction on /api/schemas and field-level sensitivity entirely.
        Definition.RequireCapability(SystemCapabilities.ManageWorkflows, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var contentType = req.ContentType ?? "Content"; // Default to generic Content type
        var variables = await extractor.GetVariablesAsync(contentType, ct);
        await Send.ResponseAsync(variables, cancellation: ct);
    }
}
