using FastEndpoints;
using FluentValidation;

namespace barakoCMS.Features.Numbering.Next;

internal sealed class NextNumberValidator : Validator<NextNumberRequest>
{
    public NextNumberValidator()
    {
        RuleFor(r => r.ContentType).NotEmpty();
    }
}
