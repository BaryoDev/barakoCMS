using FastEndpoints;
using FluentValidation;

namespace BarakoCMS.Contacts.Features.Links.Add;

internal sealed class AddLinkValidator : Validator<AddLinkRequest>
{
    public AddLinkValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.OrganizationId).NotEmpty();
        RuleFor(x => x.Role).MaximumLength(ContactSupport.MaxText);
    }
}
