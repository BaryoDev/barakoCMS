using FluentValidation;

namespace barakoCMS.Features.Auth.Register;

internal class Request
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
}

internal class RequestValidator : FastEndpoints.Validator<Request>
{
    public RequestValidator()
    {
        RuleFor(x => x.Username).NotEmpty().MinimumLength(3);
        // Password minimum length must match PasswordPolicyValidator (12 characters)
        RuleFor(x => x.Password).NotEmpty().MinimumLength(12)
            .WithMessage("Password must be at least 12 characters long.");
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
    }
}

internal class Response
{
    public string Message { get; set; } = string.Empty;
}
