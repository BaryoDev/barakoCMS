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

    // Storage for validation errors during async validation
    private string _lastValidationErrors = string.Empty;

    public RequestValidator(IQuerySession session)
    {
        _session = session;

        RuleFor(x => x.ContentType).NotEmpty();
        RuleFor(x => x.Data).NotEmpty();

        // Async validation against ContentType schema
        // The errors are cached during MustAsync to avoid blocking .Result call in WithMessage
        RuleFor(x => x)
            .MustAsync(async (req, ct) => await ValidateDataAgainstSchema(req, ct))
            .WithMessage(_ => _lastValidationErrors);
    }

    private async Task<bool> ValidateDataAgainstSchema(Request req, CancellationToken ct)
    {
        // Find the ContentType by slug (async query)
        var contentType = await _session.Query<barakoCMS.Models.ContentType>()
            .FirstOrDefaultAsync(c => c.Slug == req.ContentType, ct);

        if (contentType == null)
        {
            // ContentType doesn't exist - let the endpoint handle this error
            // (No schema means no validation rules to enforce)
            _lastValidationErrors = string.Empty;
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
