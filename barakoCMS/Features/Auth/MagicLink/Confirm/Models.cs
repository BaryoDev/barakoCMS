using FluentValidation;

namespace barakoCMS.Features.Auth.MagicLink.Confirm;

internal class MagicLinkConfirmRequest
{
    public string Token { get; set; } = string.Empty;
}

internal class MagicLinkConfirmResponse
{
    public string Token { get; set; } = string.Empty;
    public DateTime Expiry { get; set; }
    public string RefreshToken { get; set; } = string.Empty;
    public DateTime RefreshTokenExpiry { get; set; }

    /// <summary>
    /// True when the link was good but the account has MFA enabled: no tokens are issued and the
    /// client finishes at /api/auth/mfa/verify with <see cref="MfaChallengeToken"/>.
    /// </summary>
    public bool RequiresMfa { get; set; }
    public string? MfaChallengeToken { get; set; }
}

internal class MagicLinkConfirmRequestValidator : FastEndpoints.Validator<MagicLinkConfirmRequest>
{
    // The value is parsed and BCrypt-verified, so an anonymous caller does not get to hand it a megabyte.
    public MagicLinkConfirmRequestValidator()
    {
        RuleFor(x => x.Token).NotEmpty().MaximumLength(256);
    }
}
