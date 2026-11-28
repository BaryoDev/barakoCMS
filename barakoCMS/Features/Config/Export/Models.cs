using FastEndpoints;
using FluentValidation;

namespace barakoCMS.Features.Config.Export;

internal sealed class Request
{
    /// <summary>The sections to export, comma separated. Left out, every section exported by default is.</summary>
    public string? Sections { get; set; }

    /// <summary>The content types to narrow contentTypes and contents to, comma separated, matched without regard to case. Left out, every type. A name that matches none is ignored.</summary>
    public string? Types { get; set; }

    /// <summary>The type names listed, or null when none are.</summary>
    public IReadOnlyList<string>? TypeNames() =>
        string.IsNullOrWhiteSpace(Types)
            ? null
            : Types.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The sections named, or null when none are.</summary>
    public IReadOnlySet<string>? Named() =>
        string.IsNullOrWhiteSpace(Sections)
            ? null
            : Sections.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
}

/// <summary>Refuses a section this server does not know, naming the ones it does.</summary>
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
    }
}
