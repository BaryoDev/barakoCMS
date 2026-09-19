using FluentValidation;

namespace barakoCMS.Features.Content.Schedule;

internal class Request
{
    public Guid Id { get; set; }

    /// <summary>When set (UTC), a Draft is promoted to Published at/after this time. Null clears it.</summary>
    public DateTime? ScheduledPublishAt { get; set; }

    /// <summary>When set (UTC), a Published item is Archived at/after this time. Null clears it.</summary>
    public DateTime? ScheduledUnpublishAt { get; set; }

    /// <summary>
    /// The sensitivity the entry takes at <see cref="ScheduledSensitivityAt"/>. Both or neither;
    /// both null clears an armed change. The entry stays Published: this is "still published, but
    /// only these roles may read it from that moment", where an unpublish time is "gone" (#824).
    /// </summary>
    public barakoCMS.Models.SensitivityLevel? ScheduledSensitivity { get; set; }

    /// <summary>When (UTC) the sensitivity changes. Has to be in the future.</summary>
    public DateTime? ScheduledSensitivityAt { get; set; }

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
        RuleFor(x => x.ScheduledSensitivityAt)
            .Must((req, at) => (req.ScheduledSensitivity is null) == (at is null))
            .WithMessage("ScheduledSensitivity and ScheduledSensitivityAt go together: send both, or neither to clear.");
        // A publish time in the past is swept immediately and that is useful. A sensitivity time in
        // the past is a change the caller could have made directly, and arming it to fire within
        // the minute hides who decided it behind the system actor.
        RuleFor(x => x.ScheduledSensitivityAt)
            .Must(at => at is null || at > DateTime.UtcNow)
            .WithMessage("ScheduledSensitivityAt must be in the future.");
    }
}

internal class Response
{
    public string Message { get; set; } = string.Empty;
    public DateTime? ScheduledPublishAt { get; set; }
    public DateTime? ScheduledUnpublishAt { get; set; }

    /// <summary>Where the entry ended up, since arming a publish time now moves it.</summary>
    public barakoCMS.Models.ContentStatus Status { get; set; }

    public barakoCMS.Models.SensitivityLevel? ScheduledSensitivity { get; set; }
    public DateTime? ScheduledSensitivityAt { get; set; }
}
