using FastEndpoints;
using FluentValidation;

namespace barakoCMS.Features.Activity.List;

internal sealed class ListValidator : Validator<ListRequest>
{
    public ListValidator()
    {
        RuleFor(r => r.Since)
            .Must(s => ActivityCursor.TryParse(s, out var cursor) && InRange(cursor.At)
                && (cursor.Anchor is null || InRange(cursor.Anchor.Value)))
            .When(r => r.Since is not null)
            .WithMessage("Since must be a cursor from an earlier response or a timestamp.");

        RuleFor(r => r.Kinds).MaximumLength(500);
    }

    /// <summary>After the floor and no later than a day ahead, so no time near MaxValue reaches the database.</summary>
    private static bool InRange(DateTimeOffset at) =>
        at >= ActivityCursor.Floor && at <= DateTimeOffset.UtcNow.AddDays(1);
}
