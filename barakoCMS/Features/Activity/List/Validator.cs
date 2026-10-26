using FastEndpoints;
using FluentValidation;

namespace barakoCMS.Features.Activity.List;

internal sealed class ListValidator : Validator<ListRequest>
{
    public ListValidator()
    {
        RuleFor(r => r.Since)
            .Must(s => ActivityCursor.TryParse(s, out var cursor) && cursor.At >= ActivityCursor.Floor
                && (cursor.Anchor is null || cursor.Anchor >= ActivityCursor.Floor))
            .When(r => r.Since is not null)
            .WithMessage("Since must be a cursor from an earlier response or a timestamp.");

        RuleFor(r => r.Kinds).MaximumLength(500);
    }
}
