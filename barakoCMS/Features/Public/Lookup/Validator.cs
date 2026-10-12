using barakoCMS.Core.Validation;
using FastEndpoints;
using FluentValidation;

namespace barakoCMS.Features.Public.Lookup;

internal sealed class Validator : Validator<Request>
{
    public Validator()
    {
        RuleFor(x => x.Reference).NotEmpty().MaximumLength(LookupRules.MaxValueLength);
        RuleFor(x => x.Verify).NotEmpty().MaximumLength(LookupRules.MaxValueLength);
    }
}
