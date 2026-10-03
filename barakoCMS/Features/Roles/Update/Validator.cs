using FastEndpoints;
using FluentValidation;

namespace barakoCMS.Features.Roles.Update;

/// <summary>
/// The shape of the field sets on the role's rules. What they name is checked against the tenant's
/// content types in the endpoint, by <see cref="FieldSetRules.CheckAsync"/>.
/// </summary>
/// <remarks>
/// The shape of a reference condition is not checked here, unlike on create: an update passes over a
/// condition the stored role already holds, and that takes the stored role.
/// </remarks>
internal class Validator : Validator<Request>
{
    public Validator()
    {
        RuleFor(x => x.Permissions).Custom((permissions, context) =>
        {
            foreach (var error in FieldSetRules.ShapeErrors(permissions))
                context.AddFailure(error);
        });
    }
}
