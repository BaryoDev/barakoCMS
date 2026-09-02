using FluentValidation;

namespace barakoCMS.Features.Content.Schedule;

internal class Request
{
    public Guid Id { get; set; }

    /// <summary>When set (UTC), a Draft is promoted to Published at/after this time. Null clears it.</summary>
    public DateTime? ScheduledPublishAt { get; set; }

    /// <summary>When set (UTC), a Published item is Archived at/after this time. Null clears it.</summary>
    public DateTime? ScheduledUnpublishAt { get; set; }

    /// <summary>The stream version this schedule was decided against.</summary>
    /// <remarks>
    /// Zero means the client sent none, matching the update endpoint. For a document type that is
    /// the bypass it has always been. For an event-sourced type it is a refusal: arming a publish
    /// time is a decision about an item, and a decision taken against a copy that has since been
    /// edited or archived is not one the scheduler should act on days later.
    /// </remarks>
    public long Version { get; set; }
}

internal class RequestValidator : FastEndpoints.Validator<Request>
{
    public RequestValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        // If both are set, unpublishing must come strictly after publishing. Otherwise the item
        // would archive itself before (or at the moment) it goes live.
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
