using barakoCMS.Infrastructure.Viewers;
using FastEndpoints;
using FluentValidation;

namespace barakoCMS.Features.Viewers.Create;

internal sealed class CreateViewerTokenValidator : Validator<CreateViewerTokenRequest>
{
    public CreateViewerTokenValidator()
    {
        RuleFor(x => x.Subject).NotEmpty().MaximumLength(200);
        RuleFor(x => x.LifetimeMinutes)
            .InclusiveBetween(1, (int)ViewerToken.MaxLifetime.TotalMinutes)
            .When(x => x.LifetimeMinutes is not null);
    }
}
