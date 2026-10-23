using System.Text.RegularExpressions;
using FastEndpoints;
using FluentValidation;

namespace BarakoCMS.Reports.Features.Create;

internal sealed partial class Validator : Validator<Request>
{
    public Validator()
    {
        RuleFor(x => x.Name).NotEmpty().Matches(NamePattern())
            .WithMessage("A report name is lower case letters, digits and hyphens, up to 63 characters, starting with a letter or digit.");
        RuleFor(x => x.ContentType).NotEmpty();
        RuleFor(x => x.GroupLimit).InclusiveBetween(1, ReportDefinition.MaxGroupLimit).When(x => x.GroupLimit is not null);
        RuleFor(x => x.Filters).Must(f => f.Count <= 20).WithMessage("A report takes at most 20 filters.");
        RuleFor(x => x.Measures).Must(m => m.Count <= 20).WithMessage("A report takes at most 20 measures.");

        RuleForEach(x => x.Filters).ChildRules(filter =>
        {
            filter.RuleFor(f => f.Field).NotEmpty();
            filter.RuleFor(f => f.Op).Must(ReportQuery.IsOperator).WithMessage("A filter operator is eq, ne, lt, lte, gt or gte.");
            filter.RuleFor(f => f.Value).NotNull();
        });

        RuleForEach(x => x.Measures).ChildRules(measure =>
        {
            measure.RuleFor(m => m.Fn).Must(fn => ReportQuery.Functions.Contains(fn, StringComparer.Ordinal))
                .WithMessage("A measure is count, sum, avg, min or max.");
            measure.RuleFor(m => m.Field).NotEmpty().When(m => m.Fn != "count")
                .WithMessage("sum, avg, min and max need a field.");
            measure.RuleFor(m => m.Field).Empty().When(m => m.Fn == "count")
                .WithMessage("count counts entries and takes no field.");
        });

        RuleFor(x => x.Measures).Must(m => m.Select(x => x.Label).Distinct(StringComparer.Ordinal).Count() == m.Count)
            .WithMessage("Two measures have the same name. Give one an 'as'.");
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,62}$")]
    private static partial Regex NamePattern();
}
