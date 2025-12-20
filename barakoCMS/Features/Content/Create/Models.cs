using FluentValidation;
using Marten;

namespace barakoCMS.Features.Content.Create;

public class Request
{
    public string ContentType { get; set; } = string.Empty;
    public Dictionary<string, object> Data { get; set; } = new();
    public barakoCMS.Models.ContentStatus Status { get; set; } = barakoCMS.Models.ContentStatus.Draft;
    public barakoCMS.Models.SensitivityLevel Sensitivity { get; set; } = barakoCMS.Models.SensitivityLevel.Public;
}

public class RequestValidator : FastEndpoints.Validator<Request>
{
    private readonly IQuerySession _session;

    public RequestValidator(IQuerySession session)
    {
        _session = session;

        RuleFor(x => x.ContentType).NotEmpty();
        RuleFor(x => x.Data).NotEmpty();
        
        // Async validation against ContentType schema
        RuleFor(x => x)
            .MustAsync(async (req, ct) => await ValidateDataAgainstSchema(req, ct))
            .WithMessage(req => GetSchemaValidationErrors(req).Result);
    }
    
    private async Task<bool> ValidateDataAgainstSchema(Request req, CancellationToken ct)
    {
        // Find the ContentType by slug (async query)
        var contentType = await _session.Query<barakoCMS.Models.ContentType>()
            .FirstOrDefaultAsync(c => c.Slug == req.ContentType, ct);
        
        if (contentType == null)
        {
            // ContentType doesn't exist - let the endpoint handle this error
            return true; // Don't fail validation here
        }
        
        // Validate data against field definitions
        var result = barakoCMS.Core.Validation.ContentDataValidator.ValidateData(
            req.Data,
            contentType.Fields);
        
        return result.IsValid;
    }
    
    private async Task<string> GetSchemaValidationErrors(Request req)
    {
        var contentType = await _session.Query<barakoCMS.Models.ContentType>()
            .FirstOrDefaultAsync(c => c.Slug == req.ContentType);
        
        if (contentType == null)
            return $"ContentType '{req.ContentType}' not found";
        
        var result = barakoCMS.Core.Validation.ContentDataValidator.ValidateData(
            req.Data,
            contentType.Fields);
        
        return string.Join("; ", result.Errors);
    }
}

public class Response
{
    public Guid Id { get; set; }
    public string Message { get; set; } = string.Empty;
}
