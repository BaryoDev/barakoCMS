using FluentValidation;

namespace barakoCMS.Features.Content.ChangeStatus;

public class Request
{
    public Guid Id { get; set; }

    /// <summary>
    /// The status to move to. Nullable so that omitting it, or spelling the field wrong, is a 400
    /// rather than a silent move to Draft: a non-nullable enum defaults to 0, which is Draft, and
    /// IsInEnum accepts it. A caller sending {"status": 1} archived nothing and published nothing,
    /// and got back "Content status changed to Draft".
    /// </summary>
    public barakoCMS.Models.ContentStatus? NewStatus { get; set; }
}

public class RequestValidator : FastEndpoints.Validator<Request>
{
    public RequestValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.NewStatus).NotNull().WithMessage("NewStatus is required");
        RuleFor(x => x.NewStatus!.Value).IsInEnum().When(x => x.NewStatus.HasValue);
    }
}

public class Response
{
    public string Message { get; set; } = string.Empty;
}
