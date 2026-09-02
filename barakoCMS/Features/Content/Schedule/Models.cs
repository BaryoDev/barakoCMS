using FluentValidation;

namespace barakoCMS.Features.Content.Schedule;

internal class Request
{
    public Guid Id { get; set; }

    /// <summary>When set (UTC), a Draft is promoted to Published at/after this time. Null clears it.</summary>
    public DateTime? ScheduledPublishAt { get; set; }

    /// <summary>When set (UTC), a Published item is Archived at/after this time. Null clears it.</summary>
    public DateTime? ScheduledUnpublishAt { get; set; }
}

internal class RequestValidator : FastEndpoints.Validator<Request>
{
    public RequestValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        // If both are set, unpublishing must come strictly after publishing — otherwise the item would
        // archive itself before (or at the moment) it goes live.
        RuleFor(x => x.ScheduledUnpublishAt)
            .Must((req, unpub) => req.ScheduledPublishAt is null || unpub is null || unpub > req.ScheduledPublishAt)
            .WithMessage("ScheduledUnpublishAt must be after ScheduledPublishAt.");
    }
}

internal class Response
{
    public string Message { get; set; } = string.Empty;
    public DateTime? ScheduledPublishAt { get; set; }
    public DateTime? ScheduledUnpublishAt { get; set; }

    /// <summary>Where the entry ended up, since arming a publish time now moves it.</summary>
    public barakoCMS.Models.ContentStatus Status { get; set; }
}
