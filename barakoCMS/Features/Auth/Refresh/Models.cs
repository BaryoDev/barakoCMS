using FluentValidation;

namespace barakoCMS.Features.Auth.Refresh;

internal class Request
{
    public string RefreshToken { get; set; } = string.Empty;
}

internal class RequestValidator : FastEndpoints.Validator<Request>
{
    public RequestValidator()
    {
        RuleFor(x => x.RefreshToken).NotEmpty().WithMessage("Refresh token is required");
    }
}

internal class Response
{
    public string Token { get; set; } = string.Empty;
    public DateTime Expiry { get; set; }
    public string RefreshToken { get; set; } = string.Empty;
    public DateTime RefreshTokenExpiry { get; set; }
}
