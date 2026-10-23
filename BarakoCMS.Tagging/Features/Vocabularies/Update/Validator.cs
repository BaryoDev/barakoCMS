using FastEndpoints;
using FluentValidation;

namespace BarakoCMS.Tagging.Features.Vocabularies.Update;

internal sealed class Validator : Validator<Request>
{
    public Validator()
    {
        RuleFor(r => r.Name).ValidName();
        RuleFor(r => r.Description).MaximumLength(TaggingRules.MaxDescriptionLength);
    }
}
