using barakoCMS.Models;
using FastEndpoints;
using FluentValidation;

namespace barakoCMS.Features.Collections.Push;

/// <summary>
/// The shape of a push. Each entry is then validated against its content type in the endpoint, by
/// the same validator, sensitivity rule and lifecycle hooks the create and update endpoints run.
/// </summary>
internal sealed class RequestValidator : Validator<Request>
{
    public RequestValidator()
    {
        RuleFor(x => x.Type).NotEmpty();

        RuleFor(x => x.Entries)
            .NotEmpty().WithMessage("A push needs at least one entry.")
            .Must(e => e.Count <= Limits.MaxEntries)
            .WithMessage($"A push carries at most {Limits.MaxEntries} entries.");

        RuleForEach(x => x.Entries).NotEmpty().WithMessage("An entry needs at least one field.");

        // Scheduled needs a publish time and Archived is what archiveMissing is for, so a push
        // writes entries as a draft or as published.
        RuleFor(x => x.Status)
            .Must(s => s is ContentStatus.Draft or ContentStatus.Published)
            .WithMessage("Status must be Draft or Published.");
    }
}
