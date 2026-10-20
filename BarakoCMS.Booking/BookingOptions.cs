namespace BarakoCMS.Booking;

/// <summary>
/// Which content type holds the bookings and which of its fields carry them. Bound from
/// <c>Modules:Booking</c>.
/// </summary>
public sealed class BookingOptions
{
    public const string RateLimitPolicy = "booking-availability";

    /// <summary>The content type holding the bookings.</summary>
    public string ContentType { get; set; } = "booking";

    /// <summary>The field holding the id of the booked resource.</summary>
    public string ResourceField { get; set; } = "Resource";

    /// <summary>The field holding the start, an ISO 8601 date and time with a UTC offset.</summary>
    public string StartField { get; set; } = "Start";

    /// <summary>The field holding the end, in the same form. The end is exclusive.</summary>
    public string EndField { get; set; } = "End";

    /// <summary>The field holding the booking's state.</summary>
    public string StatusField { get; set; } = "Status";

    /// <summary>
    /// The states that free the slot, compared case-insensitively. A booking with no state, or any
    /// other state, holds its slot.
    /// </summary>
    public List<string> CancelledStatuses { get; set; } = ["cancelled", "canceled"];

    /// <summary>The longest range one availability request may ask for.</summary>
    public int MaxRangeDays { get; set; } = 31;

    /// <summary>The most slots one availability response carries.</summary>
    public int MaxSlots { get; set; } = 2000;

    /// <summary>Availability requests allowed per client IP in <see cref="WindowSeconds"/>.</summary>
    public int PermitLimit { get; set; } = 60;

    public int WindowSeconds { get; set; } = 60;
}
