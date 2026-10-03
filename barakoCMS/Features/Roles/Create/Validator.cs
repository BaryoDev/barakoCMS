using FastEndpoints;
using FluentValidation;

namespace barakoCMS.Features.Roles.Create;

/// <summary>
/// The shape of a condition that follows a reference. What it names is checked against the
/// tenant's content types in the endpoint, by <see cref="ReferenceConditionRules.CheckAsync"/>.
/// </summary>
internal class Validator : Validator<Request>
{
    public Validator()
    {
        RuleFor(x => x.Permissions).Custom((permissions, context) =>
        {
            foreach (var error in ReferenceConditionRules.ShapeErrors(permissions))
                context.AddFailure(error);

            foreach (var error in FieldSetRules.ShapeErrors(permissions))
                context.AddFailure(error);
        });
    }
}
