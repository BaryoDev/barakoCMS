using FluentValidation;

namespace barakoCMS.Features.Content.ChangeStatus;

public class Request
{
    public Guid Id { get; set; }
    public barakoCMS.Models.ContentStatus NewStatus { get; set; }
}

public class RequestValidator : FastEndpoints.Validator<Request>
{
    public RequestValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.NewStatus).IsInEnum();
    }
}

public class Response
{
    public string Message { get; set; } = string.Empty;
}
