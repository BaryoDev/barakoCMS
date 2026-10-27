using barakoCMS.Core.Validation;
using FastEndpoints;
using FluentValidation;

namespace barakoCMS.Features.ContentType.SetIcon;

internal sealed class Validator : Validator<Request>
{
    public Validator()
    {
        RuleFor(x => x.Icon).Custom((icon, context) =>
        {
            foreach (var error in IconRules.Check(icon).Errors)
            {
                context.AddFailure("icon", error);
            }
        });
    }
}
