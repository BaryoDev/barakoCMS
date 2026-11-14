using FastEndpoints;
using FluentValidation;

namespace barakoCMS.Features.Content.Counts;

internal sealed class Validator : Validator<Request>
{
    public Validator(TimeProvider clock)
    {
        RuleFor(r => r).Custom((r, context) =>
        {
            var (_, refusal) = CountWindow.Resolve(r.Range, r.From, r.To, r.Series, clock.GetUtcNow());
            if (refusal is not null) context.AddFailure("window", refusal);
        });
    }
}
