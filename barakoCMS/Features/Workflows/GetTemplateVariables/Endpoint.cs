using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FastEndpoints;
using Microsoft.Extensions.Logging;

namespace barakoCMS.Features.Workflows.GetTemplateVariables;

/// <summary>
/// Request to get available template variables.
/// </summary>
public class Request
{
    public string? ContentType { get; set; }
}

/// <summary>
/// Endpoint to get available template variables for a content type.
/// </summary>
public class Endpoint : Endpoint<Request, TemplateVariableCollection>
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
        AllowAnonymous(); // Allow for testing
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        try
        {
            var contentType = req.ContentType ?? "Content"; // Default to generic Content type
            var variables = await _extractor.GetVariablesAsync(contentType, ct);
            await SendAsync(variables, cancellation: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving template variables for content type {ContentType}", req.ContentType);
            await SendErrorsAsync(cancellation: ct);
        }
    }
}
