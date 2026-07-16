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
    public string RefreshToken { get; set; } = string.Empty;
    public DateTime RefreshTokenExpiry { get; set; }

    /// <summary>
    /// True when the password was correct but the device is not approved: no tokens are issued and an
    /// approval OTP has been emailed. The client should collect the code and call /api/auth/otp/verify.
    /// </summary>
    public bool RequiresDeviceApproval { get; set; }

    /// <summary>Human-readable note, e.g. the device-approval prompt.</summary>
    public string? Message { get; set; }

    /// <summary>
    /// On device approval, the account's email — where the OTP was sent — so the client can complete
    /// /api/auth/otp/verify. Only returned after the correct password was supplied.
    /// </summary>
    public string? Email { get; set; }
}
