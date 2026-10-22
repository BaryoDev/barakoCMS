using FastEndpoints;
using FluentValidation;

namespace BarakoCMS.Comments.Features.Post;

internal sealed class Validator : Validator<Request>
{
    public Validator()
    {
        RuleFor(x => x.TargetType).NotEmpty().MaximumLength(CommentLimits.MaxTargetTypeLength);
        RuleFor(x => x.TargetId).NotEmpty();
        RuleFor(x => x.Body)
            .Must(b => !string.IsNullOrWhiteSpace(b)).WithMessage("A comment cannot be empty.")
            .MaximumLength(CommentLimits.MaxBodyLength);
    }
}
