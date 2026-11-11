using barakoCMS.Features.Content.Counts;
using FastEndpoints;
using FluentValidation;

namespace barakoCMS.Features.WorkflowRuns;

internal sealed class ListRunsValidator : Validator<ListRunsRequest>
{
    public ListRunsValidator(TimeProvider clock)
    {
        RuleFor(r => r).Custom((r, context) =>
        {
            // A poll ignores the window, so a window it carries is not checked.
            if (r.ChangedSince is not null) return;

            var (_, refusal) = CountWindow.Resolve(r.Range, r.From, r.To, r.Series, clock.GetUtcNow());
            if (refusal is not null) context.AddFailure("window", refusal);
        });
    }
}
