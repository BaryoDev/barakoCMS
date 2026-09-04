using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FastEndpoints;
using Microsoft.Extensions.Logging;

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
internal class Endpoint : Endpoint<Request, TemplateVariableCollection>
{
    private readonly ITemplateVariableExtractor _extractor;
    private readonly ILogger<Endpoint> _logger;

    public Endpoint(ITemplateVariableExtractor extractor, ILogger<Endpoint> logger)
    {
        _extractor = extractor;
        _logger = logger;
    }

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
        try
        {
            var contentType = req.ContentType ?? "Content"; // Default to generic Content type
            var variables = await _extractor.GetVariablesAsync(contentType, ct);
            await Send.ResponseAsync(variables, cancellation: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving template variables for content type {ContentType}", req.ContentType);
            await Send.ErrorsAsync(cancellation: ct);
        }
    }
}
