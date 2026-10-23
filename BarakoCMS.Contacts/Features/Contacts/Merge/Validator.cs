using FastEndpoints;
using FluentValidation;

namespace BarakoCMS.Contacts.Features.Contacts.Merge;

internal sealed class MergeContactValidator : Validator<MergeContactRequest>
{
    public MergeContactValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.IntoId).NotEmpty().NotEqual(x => x.Id).WithMessage("A contact cannot be merged into itself.");
    }
}
