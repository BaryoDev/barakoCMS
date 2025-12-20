using FluentValidation;

namespace barakoCMS.Features.Content.Create;

public class Request
{
    public string ContentType { get; set; } = string.Empty;
    public Dictionary<string, object> Data { get; set; } = new();
    public barakoCMS.Models.ContentStatus Status { get; set; } = barakoCMS.Models.ContentStatus.Draft;
    public barakoCMS.Models.SensitivityLevel Sensitivity { get; set; } = barakoCMS.Models.SensitivityLevel.Public;
}

/// <summary>
/// Basic request validator - only performs synchronous validation.
/// Schema validation against ContentType is handled by the endpoint via IContentValidatorService,
/// which uses a properly scoped IQuerySession.
/// </summary>
public class RequestValidator : FastEndpoints.Validator<Request>
{
    public RequestValidator()
    {
        RuleFor(x => x.ContentType).NotEmpty().WithMessage("ContentType is required");
        RuleFor(x => x.Data).NotEmpty().WithMessage("Data is required");
    }
}

public class Response
{
    public Guid Id { get; set; }
    public string Message { get; set; } = string.Empty;
}
