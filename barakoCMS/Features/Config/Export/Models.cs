using FastEndpoints;
using FluentValidation;

namespace barakoCMS.Features.Config.Export;

internal sealed class Request
{
    /// <summary>The sections to export, comma separated. Left out, every section exported by default is.</summary>
    public string? Sections { get; set; }

    /// <summary>The most type names one export names.</summary>
    public const int MaxTypes = 100;

    /// <summary>The longest type name one export names. The type store sets no length of its own, so this only bounds the request.</summary>
    public const int MaxTypeNameLength = 200;

    /// <summary>The content types to narrow contentTypes and contents to, comma separated, matched without regard to case. Left out, every type. When given it names at least one. A name that matches none is ignored.</summary>
    public string? Types { get; set; }

    /// <summary>The type names listed, or null when <c>types</c> is left out. Empty when it is given and names none.</summary>
    public IReadOnlyList<string>? TypeNames() =>
        Types?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The sections named, or null when none are.</summary>
    public IReadOnlySet<string>? Named() =>
        string.IsNullOrWhiteSpace(Sections)
            ? null
            : Sections.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
}

/// <summary>Refuses a section this server does not know, naming the ones it does, and a types list that is blank or too long.</summary>
internal sealed class Validator : Validator<Request>
{
    public Validator()
    {
        RuleFor(r => r.Sections).Custom((_, context) =>
        {
            if (context.InstanceToValidate.Named() is not { } named)
            {
                return;
            }

            var known = Resolve<ConfigEngine>().Sections;
            foreach (var unknown in named.Where(s => !known.Contains(s)))
            {
                context.AddFailure("sections", $"'{unknown}' is not a section this server knows. Known sections: {string.Join(", ", known)}.");
            }
        });

        // A filter that was meant and names nothing would otherwise export every type, which is what
        // leaving it out does. Better a refusal than a wider export than the caller asked for.
        RuleFor(r => r.Types).Custom((_, context) =>
        {
            if (context.InstanceToValidate.TypeNames() is not { } names)
            {
                return;
            }

            if (names.Count == 0)
            {
                context.AddFailure("types", "types must name at least one content type. Leave it out to export every type.");
            }
            else if (names.Count > Request.MaxTypes)
            {
                context.AddFailure("types", $"types names {names.Count} content types and at most {Request.MaxTypes} are allowed. Export fewer at a time.");
            }
            else if (names.Any(n => n.Length > Request.MaxTypeNameLength))
            {
                context.AddFailure("types", $"A name in types is longer than {Request.MaxTypeNameLength} characters.");
            }
        });
    }
}
