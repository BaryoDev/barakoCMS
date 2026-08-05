using FluentValidation;

namespace barakoCMS.Features.Auth.Mfa;

// --- Enrollment (authenticated) ---

public class SetupResponse
{
    /// <summary>Base32 TOTP secret, shown once so the user can key it in manually.</summary>
    public string Secret { get; set; } = string.Empty;
    /// <summary>otpauth:// URI to render as a QR code.</summary>
    public string OtpauthUri { get; set; } = string.Empty;
}

public class CodeRequest
{
    public string Code { get; set; } = string.Empty;
}

public class CodeRequestValidator : FastEndpoints.Validator<CodeRequest>
{
    public CodeRequestValidator() => RuleFor(x => x.Code).NotEmpty();
}

public class EnableResponse
{
    public string Message { get; set; } = string.Empty;
    /// <summary>One-time recovery codes, shown once. The client must tell the user to save them.</summary>
    public List<string> RecoveryCodes { get; set; } = new();
}

public class StatusResponse
{
    public bool Enabled { get; set; }
}

public class MessageResponse
{
    public string Message { get; set; } = string.Empty;
}

// --- Login step-up (anonymous) ---

public class VerifyRequest
{
    /// <summary>The challenge token returned by /api/auth/login when RequiresMfa is true.</summary>
    public string ChallengeToken { get; set; } = string.Empty;
    /// <summary>A TOTP from the authenticator app, or a recovery code.</summary>
    public string Code { get; set; } = string.Empty;
}

public class VerifyRequestValidator : FastEndpoints.Validator<VerifyRequest>
{
    public VerifyRequestValidator()
    {
        RuleFor(x => x.ChallengeToken).NotEmpty();
        RuleFor(x => x.Code).NotEmpty();
    }
}

public class VerifyResponse
{
    public string Token { get; set; } = string.Empty;
    public DateTime Expiry { get; set; }
    public string RefreshToken { get; set; } = string.Empty;
    public DateTime RefreshTokenExpiry { get; set; }
}
