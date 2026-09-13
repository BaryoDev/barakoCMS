using FastEndpoints;
using FluentValidation;

namespace barakoCMS.Features.Content.GetBySlug;

internal class Validator : Validator<Request>
{
    public Validator()
    {
        RuleFor(x => x.Type).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Slug).NotEmpty().MaximumLength(500);
    }
}
