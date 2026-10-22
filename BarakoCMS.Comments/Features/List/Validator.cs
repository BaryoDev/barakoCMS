using FastEndpoints;
using FluentValidation;

namespace BarakoCMS.Comments.Features.List;

internal sealed class Validator : Validator<Request>
{
    public Validator()
    {
        RuleFor(x => x.TargetType).NotEmpty().MaximumLength(CommentLimits.MaxTargetTypeLength);
        RuleFor(x => x.TargetId).NotEmpty();
    }
}
