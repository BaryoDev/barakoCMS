using barakoCMS.Core.Validation;
using FastEndpoints;
using FluentValidation;

namespace barakoCMS.Features.ContentType.SetLookup;

/// <summary>The shape of a lookup. What it may name on the type is checked against the type itself, in <see cref="LookupRules"/>.</summary>
internal sealed class Validator : Validator<Request>
{
    public Validator()
    {
        When(x => x.Lookup is not null, () =>
        {
            RuleFor(x => x.Lookup!.MatchField).NotEmpty().MaximumLength(LookupRules.MaxNameLength)
                .OverridePropertyName("lookup.matchField");

            RuleFor(x => x.Lookup!.VerifyFields)
                .Must(f => f is { Count: > 0 and <= LookupRules.MaxVerifyFields })
                .WithMessage($"lookup.verifyFields needs 1 to {LookupRules.MaxVerifyFields} fields. Without one the reference alone would return the entry.")
                .Must(Distinct).WithMessage("lookup.verifyFields names a field twice.")
                .OverridePropertyName("lookup.verifyFields");

            RuleFor(x => x.Lookup!.ReturnFields)
                .Must(f => f is { Count: > 0 and <= LookupRules.MaxReturnFields })
                .WithMessage($"lookup.returnFields needs 1 to {LookupRules.MaxReturnFields} fields.")
                .Must(Distinct).WithMessage("lookup.returnFields names a field twice.")
                .OverridePropertyName("lookup.returnFields");

            When(x => x.Lookup!.Reveal is not null, () =>
            {
                RuleFor(x => x.Lookup!.Reveal!.Fields)
                    .Must(f => f is { Count: > 0 and <= LookupRules.MaxRevealFields })
                    .WithMessage($"lookup.reveal.fields needs 1 to {LookupRules.MaxRevealFields} fields.")
                    .Must(Distinct).WithMessage("lookup.reveal.fields names a field twice.")
                    .OverridePropertyName("lookup.reveal.fields");
                RuleFor(x => x.Lookup!.Reveal!.SettingType).NotEmpty().MaximumLength(LookupRules.MaxNameLength)
                    .OverridePropertyName("lookup.reveal.settingType");
                RuleFor(x => x.Lookup!.Reveal!.SettingField).NotEmpty().MaximumLength(LookupRules.MaxNameLength)
                    .OverridePropertyName("lookup.reveal.settingField");
            });
        });
    }

    private static bool Distinct(List<string>? names) =>
        names is null
        || (names.All(n => !string.IsNullOrWhiteSpace(n) && n.Length <= LookupRules.MaxNameLength)
            && names.Distinct(StringComparer.OrdinalIgnoreCase).Count() == names.Count);
}
