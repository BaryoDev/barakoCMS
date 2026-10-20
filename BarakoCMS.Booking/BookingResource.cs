namespace BarakoCMS.Booking;

/// <summary>A bookable thing: a room, a person, a chair. Bookings point at it by id.</summary>
public sealed class BookingResource
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>The content type of the entry this resource stands for, when it stands for one.</summary>
    public string? EntryType { get; set; }

    /// <summary>The entry this resource stands for, such as a staff member or a room page.</summary>
    public Guid? EntryId { get; set; }

    /// <summary>An IANA time zone id, such as <c>Asia/Manila</c>. Opening hours are read in it.</summary>
    public string TimeZone { get; set; } = "UTC";

    /// <summary>
    /// The length of one slot in minutes. Null means free-form: availability answers the open
    /// stretches between bookings instead of fixed slots.
    /// </summary>
    public int? SlotMinutes { get; set; }

    /// <summary>An inactive resource is not offered and takes no new booking.</summary>
    public bool Active { get; set; } = true;

    public List<OpeningWindow> OpeningHours { get; set; } = [];

    public List<ResourceException> Exceptions { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>One stretch of a weekday the resource is open, in the resource's time zone.</summary>
public sealed class OpeningWindow
{
    /// <summary>A weekday name, such as <c>Monday</c>.</summary>
    public string Day { get; set; } = string.Empty;

    /// <summary><c>HH:mm</c>.</summary>
    public string Start { get; set; } = string.Empty;

    /// <summary><c>HH:mm</c>, or <c>24:00</c> for the end of the day. Exclusive.</summary>
    public string End { get; set; } = string.Empty;
}

/// <summary>One stretch of a single day, as an exception carries it.</summary>
public sealed class DayWindow
{
    public string Start { get; set; } = string.Empty;

    public string End { get; set; } = string.Empty;
}

/// <summary>A date on which the weekly hours do not apply.</summary>
public sealed class ResourceException
{
    /// <summary><c>yyyy-MM-dd</c>, a date in the resource's time zone.</summary>
    public string Date { get; set; } = string.Empty;

    /// <summary>Closed all day. When false, <see cref="Hours"/> replace the weekly hours for the date.</summary>
    public bool Closed { get; set; } = true;

    public List<DayWindow> Hours { get; set; } = [];
}
