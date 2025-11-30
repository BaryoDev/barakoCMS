using FluentValidation;

namespace barakoCMS.Features.Auth.Register;

public class Request
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
}

public class RequestValidator : FastEndpoints.Validator<Request>
{
    public RequestValidator()
    {
        RuleFor(x => x.Username).NotEmpty().MinimumLength(3);
        RuleFor(x => x.Password).NotEmpty().MinimumLength(6);
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
    }
}

public class Response
{
    public string Message { get; set; } = string.Empty;
}
