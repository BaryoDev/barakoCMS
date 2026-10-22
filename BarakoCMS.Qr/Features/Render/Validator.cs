using FastEndpoints;
using FluentValidation;
using Microsoft.Extensions.Options;

namespace BarakoCMS.Qr.Features.Render;

internal sealed class Validator : Validator<Request>
{
    public Validator(IOptions<QrOptions> options)
    {
        var max = options.Value.MaxPixelsPerModule;

        RuleFor(x => x.Format)
            .Must(f => f is null || f.Equals("png", StringComparison.OrdinalIgnoreCase)
                                 || f.Equals("svg", StringComparison.OrdinalIgnoreCase))
            .WithMessage("format must be png or svg.");

        RuleFor(x => x.Size)
            .InclusiveBetween(1, max)
            .When(x => x.Size is not null)
            .WithMessage($"size must be between 1 and {max}.");
    }
}
