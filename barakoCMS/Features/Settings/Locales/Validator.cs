using System.Text.RegularExpressions;
using FastEndpoints;
using FluentValidation;

namespace barakoCMS.Features.Settings.Locales;

internal sealed partial class UpdateLocaleSettingsValidator : Validator<UpdateLocaleSettingsRequest>
{
    internal const int MaxLocales = 50;
    internal const int MaxChainLength = 10;

    [GeneratedRegex("^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8}){0,3}$")]
    private static partial Regex CodeShape();

    public UpdateLocaleSettingsValidator()
    {
        RuleFor(x => x.Locales).NotNull().Must(l => l is null || l.Count <= MaxLocales)
            .WithMessage($"At most {MaxLocales} locales.");
        RuleFor(x => x.FallbackChains).NotNull();

        RuleForEach(x => x.Locales).Must(IsCode)
            .WithMessage("'{PropertyValue}' is not a language code like 'en' or 'pt-BR'.");
        RuleFor(x => x.Locales)
            .Must(l => l.Select(c => c.ToLowerInvariant()).Distinct().Count() == l.Count)
            .When(x => x.Locales is not null && x.Locales.All(IsCode))
            .WithMessage("Locales must not repeat.");

        RuleFor(x => x.DefaultLocale).NotNull()
            .Must((req, d) => req.Locales is null || (req.Locales.Count == 0 ? d.Length == 0 : Contains(req.Locales, d)))
            .WithMessage("DefaultLocale must be one of Locales, and empty only when Locales is empty.");

        RuleFor(x => x.FallbackChains).Custom((req, ctx) =>
        {
            var locales = ctx.InstanceToValidate.Locales ?? [];
            foreach (var (code, chain) in req)
            {
                if (!Contains(locales, code))
                    ctx.AddFailure($"FallbackChains: '{code}' is not in Locales.");
                if (chain is null || chain.Count > MaxChainLength)
                {
                    ctx.AddFailure($"FallbackChains: the chain for '{code}' must have at most {MaxChainLength} entries.");
                    continue;
                }
                foreach (var entry in chain.Where(e => !Contains(locales, e)))
                    ctx.AddFailure($"FallbackChains: '{entry}' (in the chain for '{code}') is not in Locales.");
            }

            if (HasCycle(req))
                ctx.AddFailure("FallbackChains must not loop back on themselves.");
        });
    }

    private static bool IsCode(string? c) => c is not null && CodeShape().IsMatch(c);

    private static bool Contains(IEnumerable<string> locales, string? code) =>
        code is not null && locales.Contains(code, StringComparer.OrdinalIgnoreCase);

    private static bool HasCycle(Dictionary<string, List<string>> chains)
    {
        var edges = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (code, chain) in chains)
            edges[code] = chain ?? [];

        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        bool Visit(string node)
        {
            if (state.TryGetValue(node, out var s)) return s == 1;
            state[node] = 1;
            if (edges.TryGetValue(node, out var next) && next.Any(Visit)) return true;
            state[node] = 2;
            return false;
        }

        return edges.Keys.ToList().Any(Visit);
    }
}
