using FastEndpoints;
using FluentValidation;

namespace barakoCMS.Features.Roles.Preview;

internal class Validator : Validator<Request>
{
    public Validator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.ContentType).NotEmpty();

        RuleFor(x => x.Permissions).Custom((permissions, context) =>
        {
            foreach (var error in FieldSetRules.ShapeErrors(permissions))
                context.AddFailure(error);
        });
    }
}
