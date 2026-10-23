using FastEndpoints;
using FluentValidation;

namespace BarakoCMS.Tagging.Features.Terms.Update;

internal sealed class Validator : Validator<Request>
{
    public Validator()
    {
        RuleFor(r => r.Name).ValidName();
    }
}
