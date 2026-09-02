using FluentValidation;

namespace barakoCMS.Features.Content.ChangeStatus;

internal class Request
{
    public Guid Id { get; set; }

    /// <summary>
    /// The status to move to. Nullable so that omitting it, or spelling the field wrong, is a 400
    /// rather than a silent move to Draft: a non-nullable enum defaults to 0, which is Draft, and
    /// IsInEnum accepts it. A caller sending {"status": 1} archived nothing and published nothing,
    /// and got back "Content status changed to Draft".
    /// </summary>
    public barakoCMS.Models.ContentStatus? NewStatus { get; set; }

    /// <summary>
    /// The named transition to perform, for a content type that declares its own lifecycle.
    /// </summary>
    /// <remarks>
    /// The two fields are alternatives, not a pair. A type with no lifecycle takes NewStatus, which
    /// is every type that exists today. A type with one takes Transition, by name, because the name
    /// is what a permission and a workflow key on and a target state alone cannot express which move
    /// was made when two transitions share a destination.
    ///
    /// Sending both, or the wrong one for the type, is refused rather than resolved by precedence. A
    /// caller who sends NewStatus to a type with a lifecycle has misunderstood something, and
    /// picking one silently would hide it.
    /// </remarks>
    public string? Transition { get; set; }
}

internal class RequestValidator : FastEndpoints.Validator<Request>
{
    public RequestValidator()
    {
        RuleFor(x => x.Id).NotEmpty();

        // One or the other, never both and never neither. Which one is correct depends on the
        // content type, which this validator cannot see, so the endpoint decides that and this only
        // rules out the shapes that are wrong for every type.
        RuleFor(x => x)
            .Must(r => r.NewStatus.HasValue ^ !string.IsNullOrWhiteSpace(r.Transition))
            .WithMessage("Send either NewStatus or Transition, not both and not neither.");

        RuleFor(x => x.NewStatus!.Value).IsInEnum().When(x => x.NewStatus.HasValue);
    }
}

internal class Response
{
    public string Message { get; set; } = string.Empty;
}
