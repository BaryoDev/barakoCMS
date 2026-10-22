using FluentValidation;

namespace barakoCMS.Features.Content.Create;

internal class Request
{
    public string ContentType { get; set; } = string.Empty;
    public Dictionary<string, object> Data { get; set; } = new();
    public barakoCMS.Models.ContentStatus Status { get; set; } = barakoCMS.Models.ContentStatus.Draft;
    public barakoCMS.Models.SensitivityLevel Sensitivity { get; set; } = barakoCMS.Models.SensitivityLevel.Public;

    /// <summary>
    /// Check the write with everything the real write runs and answer with what it would change,
    /// storing nothing.
    /// </summary>
    public bool DryRun { get; set; }

    /// <summary>
    /// The entries of the type's line type to write with this one, in one transaction. Refused for a
    /// type that declares no lines. Each takes this entry's status and sensitivity, and the line
    /// type's parent field is set to this entry's id whatever is sent there.
    /// </summary>
    public List<Dictionary<string, object>>? Lines { get; set; }
}

/// <summary>
/// Basic request validator - only performs synchronous validation.
/// Schema validation against ContentType is handled by the endpoint via IContentValidatorService,
/// which uses a properly scoped IQuerySession.
/// </summary>
internal class RequestValidator : FastEndpoints.Validator<Request>
{
    public const int MaxLines = LineTotals.MaxLines;

    public RequestValidator()
    {
        RuleFor(x => x.ContentType).NotEmpty().WithMessage("ContentType is required");
        RuleFor(x => x.Data).NotEmpty().WithMessage("Data is required");

        // A number outside the enum binds cleanly and stores content with an undefined status,
        // which is then invisible to the scheduler, to status-filtered lists and to delivery, with
        // no error anywhere. ChangeStatus has checked this since it was written; Create did not.
        RuleFor(x => x.Status).IsInEnum().WithMessage("Status is not a valid value");
        RuleFor(x => x.Sensitivity).IsInEnum().WithMessage("Sensitivity is not a valid value");
        RuleFor(x => x.Lines).Must(l => l is null || l.Count <= MaxLines)
            .WithMessage($"Lines holds at most {MaxLines} entries");
    }
}

internal class Response : barakoCMS.Core.Validation.IDryRunResponse
{
    public Guid Id { get; set; }
    /// <summary>
    /// Initial event-stream version (1). Echo it back in an update's Version field for concurrency checks.
    /// </summary>
    public long Version { get; set; }

    /// <summary>The ids of the lines written with the entry, in the order sent. An empty list when the entry was written through the lines path with none, null when it was not.</summary>
    public List<Guid>? LineIds { get; init; }

    /// <summary>True when this answers a dry run: nothing was stored.</summary>
    public bool DryRun { get; init; }

    /// <summary>For a dry run, what the write would change. Null otherwise.</summary>
    public List<barakoCMS.Core.Validation.FieldDiff>? Changes { get; init; }
}
