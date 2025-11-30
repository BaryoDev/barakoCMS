using FluentValidation;

namespace barakoCMS.Features.Content.Update;

public class Request
{
    public Guid Id { get; set; }
    public Dictionary<string, object> Data { get; set; } = new();
}

public class RequestValidator : FastEndpoints.Validator<Request>
{
    public RequestValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Data).NotEmpty();
    }
}

public class Response
{
    public Guid Id { get; set; }
    public string Message { get; set; } = string.Empty;
}
