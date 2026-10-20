using FluentValidation;

namespace barakoCMS.Features.Users.ResetMfa;

internal class Validator : FastEndpoints.Validator<Request>
{
    public const int MaxReasonLength = 500;

    public Validator()
    {
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(MaxReasonLength);
    }
}
