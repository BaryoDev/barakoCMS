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
        // No longer required in the body. A browser sends it as an httpOnly cookie the page
        // cannot read, and the endpoint falls back to that. Requiring it here would refuse the
        // request before the cookie was ever looked at.
        //
        // Missing in both places is still refused, in the endpoint, with the same 401 an unknown
        // token gets: the two are indistinguishable to a caller on purpose.
    }
}

internal class Response
{
    public string Token { get; set; } = string.Empty;
    public DateTime Expiry { get; set; }
    public string RefreshToken { get; set; } = string.Empty;
    public DateTime RefreshTokenExpiry { get; set; }
}
