using FluentValidation;
using Marten;

namespace barakoCMS.Features.Content.Update;

public class Request
{
    public Guid Id { get; set; }
    public Dictionary<string, object> Data { get; set; } = new();
    public Models.ContentStatus Status { get; set; }
    public long Version { get; set; }
}

public class RequestValidator : FastEndpoints.Validator<Request>
{
    private readonly IDocumentSession _session;
    
    // Thread-safe storage for validation errors during async validation
    private string _lastValidationErrors = string.Empty;

    public RequestValidator(IDocumentSession session)
    {
        _session = session;

        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Data).NotEmpty();

        // Async validation against existing Content's ContentType
        // The errors are cached during MustAsync to avoid blocking .Result call in WithMessage
        RuleFor(x => x)
            .MustAsync(async (req, ct) => await ValidateDataAgainstExistingContentType(req, ct))
            .WithMessage(_ => _lastValidationErrors);
    }

    private async Task<bool> ValidateDataAgainstExistingContentType(Request req, CancellationToken ct)
    {
        // Find the existing content
        var content = await _session.LoadAsync<barakoCMS.Models.Content>(req.Id, ct);

        if (content == null)
        {
            // Content doesn't exist - let the endpoint handle this error
            _lastValidationErrors = $"Content with ID '{req.Id}' not found";
            return true;
        }

        // Find the ContentType (async query)
        var contentType = await _session.Query<barakoCMS.Models.ContentType>()
            .FirstOrDefaultAsync(c => c.Slug == content.ContentType, ct);

        if (contentType == null)
        {
            // ContentType doesn't exist (shouldn't happen)
            _lastValidationErrors = $"ContentType '{content.ContentType}' not found";
            return true;
        }

        // Validate data against field definitions
        var result = barakoCMS.Core.Validation.ContentDataValidator.ValidateData(
            req.Data,
            contentType.Fields);

        // Cache errors for WithMessage to use (avoids blocking .Result call)
        _lastValidationErrors = result.IsValid 
            ? string.Empty 
            : string.Join("; ", result.Errors);

        return result.IsValid;
    }
}

public class Response
{
    public Guid Id { get; set; }
    public string Message { get; set; } = string.Empty;
}
