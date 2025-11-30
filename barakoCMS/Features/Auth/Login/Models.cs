using FluentValidation;

namespace barakoCMS.Features.Auth.Login;

public class Request
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public class RequestValidator : FastEndpoints.Validator<Request>
{
    public RequestValidator()
    {
        RuleFor(x => x.Username).NotEmpty();
        RuleFor(x => x.Password).NotEmpty();
    }
}

public class Response
{
    public string Token { get; set; } = string.Empty;
    public DateTime Expiry { get; set; }
}
