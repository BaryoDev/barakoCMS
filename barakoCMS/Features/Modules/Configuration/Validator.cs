using FastEndpoints;
using FluentValidation;

namespace barakoCMS.Features.Modules.Configuration;

internal sealed class Validator : Validator<ModuleConfigurationRequest>
{
    public const int MaxNameLength = 100;

    public Validator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(MaxNameLength);
    }
}
