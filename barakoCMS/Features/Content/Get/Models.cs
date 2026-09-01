namespace barakoCMS.Features.Content.Get;

internal class Request
{
    public Guid Id { get; set; }
}

internal class Response
{
    public Guid Id { get; set; }
    public string ContentType { get; set; } = string.Empty;
    public Dictionary<string, object> Data { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public barakoCMS.Models.ContentStatus Status { get; set; }
    public Guid LastModifiedBy { get; set; }
    public barakoCMS.Models.SensitivityLevel Sensitivity { get; set; }

    /// <summary>When the scheduler will publish this, or null if nothing is armed. Always UTC.</summary>
    /// <remarks>
    /// Returned so a client can show what is armed. Arming a publish time through
    /// <c>PUT /api/contents/{id}/schedule</c> and then having no way to read it back means the only
    /// way to know is to wait and see whether it happened.
    ///
    /// DateTimeOffset rather than the DateTime the document stores, so the value carries a zone on
    /// the wire. That rule is asserted across endpoints by DateWireFormatTests.
    /// </remarks>
    public DateTimeOffset? ScheduledPublishAt { get; set; }

    /// <summary>When the scheduler will archive this, or null if nothing is armed. Always UTC.</summary>
    public DateTimeOffset? ScheduledUnpublishAt { get; set; }
    /// <summary>
    /// Event-stream version. Send this back in an update's Version field for optimistic concurrency.
    /// </summary>
    public long Version { get; set; }
}
