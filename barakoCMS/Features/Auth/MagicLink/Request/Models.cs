using FluentValidation;

namespace barakoCMS.Features.Auth.MagicLink.Request;

internal class MagicLinkRequest
{
    public string Email { get; set; } = string.Empty;
}

internal class MagicLinkRequestResponse
{
    public string Message { get; set; } = string.Empty;
}

internal class MagicLinkRequestValidator : FastEndpoints.Validator<MagicLinkRequest>
{
    public MagicLinkRequestValidator()
    {
        // Only a bound. A malformed address is answered like an unknown one by the endpoint, so
        // that the response never says which addresses exist.
        RuleFor(x => x.Email).MaximumLength(254);
    }
}
