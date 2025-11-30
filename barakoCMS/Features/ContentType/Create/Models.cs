using FluentValidation;

namespace barakoCMS.Features.ContentType.Create;

public class Request
{
    public string Name { get; set; } = string.Empty;
    public Dictionary<string, string> Fields { get; set; } = new();
}

public class RequestValidator : FastEndpoints.Validator<Request>
{
    public RequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty();
        RuleFor(x => x.Fields).NotEmpty();
    }
}

public class Response
{
    public Guid Id { get; set; }
    public string Message { get; set; } = string.Empty;
}
