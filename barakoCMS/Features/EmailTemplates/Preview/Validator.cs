using FastEndpoints;
using FluentValidation;

namespace barakoCMS.Features.EmailTemplates.Preview;

internal sealed class RequestValidator : Validator<Request>
{
    public RequestValidator()
    {
        RuleFor(r => r.Id)
            .NotEmpty()
            .MaximumLength(EmailTemplateRenderer.MaxNameLength);

        RuleFor(r => r.EntryId)
            .NotEmpty()
            .WithMessage("entryId names the entry the template is previewed against.");
    }
}
