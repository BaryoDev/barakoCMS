using FluentValidation;

namespace barakoCMS.Features.Content.Create;

internal class Request
{
    public string ContentType { get; set; } = string.Empty;
    public Dictionary<string, object> Data { get; set; } = new();
    public barakoCMS.Models.ContentStatus Status { get; set; } = barakoCMS.Models.ContentStatus.Draft;
    public barakoCMS.Models.SensitivityLevel Sensitivity { get; set; } = barakoCMS.Models.SensitivityLevel.Public;
}

/// <summary>
/// Basic request validator - only performs synchronous validation.
/// Schema validation against ContentType is handled by the endpoint via IContentValidatorService,
/// which uses a properly scoped IQuerySession.
/// </summary>
internal class RequestValidator : FastEndpoints.Validator<Request>
{
    public RequestValidator()
    {
        RuleFor(x => x.ContentType).NotEmpty().WithMessage("ContentType is required");
        RuleFor(x => x.Data).NotEmpty().WithMessage("Data is required");

        // A number outside the enum binds cleanly and stores content with an undefined status,
        // which is then invisible to the scheduler, to status-filtered lists and to delivery, with
        // no error anywhere. ChangeStatus has checked this since it was written; Create did not.
        RuleFor(x => x.Status).IsInEnum().WithMessage("Status is not a valid value");
        RuleFor(x => x.Sensitivity).IsInEnum().WithMessage("Sensitivity is not a valid value");
    }
}

internal class Response
{
    public Guid Id { get; set; }
    /// <summary>
    /// Initial event-stream version (1). Echo it back in an update's Version field for concurrency checks.
    /// </summary>
    public long Version { get; set; }
}
