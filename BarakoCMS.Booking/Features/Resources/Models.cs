namespace BarakoCMS.Booking.Features.Resources;

/// <summary>What a caller sends to create or replace a resource.</summary>
internal sealed class ResourceRequest
{
    public string Name { get; set; } = string.Empty;

    public string? EntryType { get; set; }

    public Guid? EntryId { get; set; }

    /// <summary>An IANA time zone id, such as <c>Asia/Manila</c>.</summary>
    public string TimeZone { get; set; } = string.Empty;

    /// <summary>Minutes per slot, 5 to 1440, or null for free-form.</summary>
    public int? SlotMinutes { get; set; }

    public bool Active { get; set; } = true;

    public List<OpeningWindow> OpeningHours { get; set; } = [];

    public List<ResourceException> Exceptions { get; set; } = [];
}

internal sealed class ResourceResponse
{
    public int Contract { get; set; } = BookingContract.Version;

    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? EntryType { get; set; }

    public Guid? EntryId { get; set; }

    public string TimeZone { get; set; } = string.Empty;

    public int? SlotMinutes { get; set; }

    public bool Active { get; set; }

    public List<OpeningWindow> OpeningHours { get; set; } = [];

    public List<ResourceException> Exceptions { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public static ResourceResponse From(BookingResource r) => new()
    {
        Id = r.Id,
        Name = r.Name,
        EntryType = r.EntryType,
        EntryId = r.EntryId,
        TimeZone = r.TimeZone,
        SlotMinutes = r.SlotMinutes,
        Active = r.Active,
        OpeningHours = r.OpeningHours,
        Exceptions = r.Exceptions,
        CreatedAt = r.CreatedAt,
        UpdatedAt = r.UpdatedAt,
    };
}
