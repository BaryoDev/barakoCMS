using FastEndpoints;
using FluentValidation;

namespace BarakoCMS.Tagging.Features.Assignments.ForRecord;

internal sealed class Validator : Validator<Request>
{
    public Validator()
    {
        RuleFor(r => r.ContentType).ValidContentType();
        RuleFor(r => r.RecordId).ValidRecordId();
    }
}
