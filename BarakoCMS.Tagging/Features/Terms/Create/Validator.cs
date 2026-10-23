using FastEndpoints;
using FluentValidation;

namespace BarakoCMS.Tagging.Features.Terms.Create;

internal sealed class Validator : Validator<Request>
{
    public Validator()
    {
        RuleFor(r => r.Key).ValidKey();
        RuleFor(r => r.Name).ValidName();
    }
}
