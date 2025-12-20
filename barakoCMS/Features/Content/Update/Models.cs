using FluentValidation;

namespace barakoCMS.Features.Content.Update;

public class Request
{
    public Guid Id { get; set; }
    public Dictionary<string, object> Data { get; set; } = new();
    public Models.ContentStatus Status { get; set; }
    public long Version { get; set; }
}

/// <summary>
/// Basic request validator - only performs synchronous validation.
/// Content existence and schema validation are handled by the endpoint
/// using properly scoped IDocumentSession instances.
/// </summary>
public class RequestValidator : FastEndpoints.Validator<Request>
{
    public RequestValidator()
    {
        RuleFor(x => x.Id).NotEmpty().WithMessage("Content ID is required");
        RuleFor(x => x.Data).NotEmpty().WithMessage("Data is required");
    }
}

public class Response
{
    public Guid Id { get; set; }
    public string Message { get; set; } = string.Empty;
}
