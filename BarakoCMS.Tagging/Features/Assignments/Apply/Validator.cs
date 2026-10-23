using FastEndpoints;
using FluentValidation;

namespace BarakoCMS.Tagging.Features.Assignments.Apply;

internal sealed class Validator : Validator<Request>
{
    public Validator()
    {
        RuleFor(r => r.ContentType).ValidContentType();
        RuleFor(r => r.RecordId).ValidRecordId();
        RuleFor(r => r.TermId).NotEmpty();
    }
}
