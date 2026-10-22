using FastEndpoints;
using FluentValidation;

namespace barakoCMS.Features.Tenants.Export;

internal sealed class Request
{
    /// <summary>The handle of the tenant to export.</summary>
    public string Handle { get; set; } = string.Empty;
}

internal sealed class Validator : Validator<Request>
{
    public Validator()
    {
        RuleFor(r => r.Handle).NotEmpty().WithMessage("A tenant handle is needed.");
    }
}
