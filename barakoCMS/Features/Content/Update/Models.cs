using FluentValidation;

namespace barakoCMS.Features.Content.Update;

internal class Request
{
    public Guid Id { get; set; }
    public Dictionary<string, object> Data { get; set; } = new();

    /// <summary>
    /// The status to leave the content in. Nullable so that omitting it means "unchanged".
    /// </summary>
    /// <remarks>
    /// A non-nullable enum defaults to 0, which is Draft, and the endpoint treats any difference
    /// from the stored status as a transition. A consumer sending only id, data and version, which
    /// is what a data-only edit looks like, therefore un-published the item and emitted a
    /// ContentStatusChanged saying so. Same defaulting trap as ChangeStatus.NewStatus.
    /// </remarks>
    public Models.ContentStatus? Status { get; set; }

    public long Version { get; set; }
}

/// <summary>
/// Basic request validator - only performs synchronous validation.
/// Content existence and schema validation are handled by the endpoint
/// using properly scoped IDocumentSession instances.
/// </summary>
internal class RequestValidator : FastEndpoints.Validator<Request>
{
    public RequestValidator()
    {
        RuleFor(x => x.Id).NotEmpty().WithMessage("Content ID is required");
        RuleFor(x => x.Data).NotEmpty().WithMessage("Data is required");

        // A number outside the enum bound cleanly and stored an undefined status. Only checked when
        // a status was actually sent: absent means unchanged, which is the point of the nullable.
        RuleFor(x => x.Status!.Value).IsInEnum().WithMessage("Status is not a valid value")
            .When(x => x.Status.HasValue);
    }
}

internal class Response
{
    public Guid Id { get; set; }
    /// <summary>
    /// The content's event-stream version after this update. Echo it back in the next
    /// update's Version field to enable optimistic-concurrency conflict detection.
    /// </summary>
    public long Version { get; set; }
}
