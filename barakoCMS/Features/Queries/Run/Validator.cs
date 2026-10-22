using FastEndpoints;
using FluentValidation;

namespace barakoCMS.Features.Queries.Run;

internal sealed class Validator : Validator<Request>
{
    public Validator()
    {
        RuleFor(r => r.Slug).Must(QueryGate.IsSlug)
            .WithMessage("That is not a query slug: lowercase letters, digits and hyphens only.");

        RuleFor(r => r.From).Must(IsDay).When(r => r.From is not null)
            .WithMessage("from must be written yyyy-MM-dd.");

        RuleFor(r => r.To).Must(IsDay).When(r => r.To is not null)
            .WithMessage("to must be written yyyy-MM-dd.");
    }

    private static bool IsDay(string? value) =>
        string.IsNullOrWhiteSpace(value)
        || DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _);
}
