using System.Globalization;
using System.Text.RegularExpressions;
using FastEndpoints;
using Marten;
// RequireRateLimiting lives here; this project is not a Web SDK project, so it is not implicitly used.
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Options;

namespace BarakoCMS.Booking.Features.Availability;

internal sealed class Request
{
    /// <summary>The id of the resource.</summary>
    public string? Resource { get; set; }

    /// <summary>Start of the range, ISO 8601 with a UTC offset. A <c>+</c> in the offset must be sent as %2B.</summary>
    public string? From { get; set; }

    /// <summary>End of the range, exclusive, in the same form.</summary>
    public string? To { get; set; }
}

internal sealed record FreeSlot(DateTimeOffset Start, DateTimeOffset End);

/// <param name="Contract">The module's HTTP contract.</param>
/// <param name="Resource">The id asked about.</param>
/// <param name="TimeZone">The resource's IANA time zone. Each slot's offset is the one in force in it.</param>
/// <param name="SlotMinutes">The slot length, or null when the resource is free-form and slots are open stretches.</param>
/// <param name="Slots">The free slots, earliest first.</param>
/// <param name="Truncated">True when there were more free slots than one response carries.</param>
internal sealed record Response(int Contract, Guid Resource, string TimeZone, int? SlotMinutes, bool Truncated, IReadOnlyList<FreeSlot> Slots);

/// <summary>
/// GET /api/public/availability?resource=&amp;from=&amp;to=. Anonymous. The free slots of a resource in
/// a range, and nothing about the bookings that took the rest.
/// </summary>
/// <remarks>
/// The range is at most <see cref="BookingOptions.MaxRangeDays"/> days. An unknown or inactive
/// resource answers 404, the same for both. Throttled per client IP.
/// </remarks>
internal sealed partial class Endpoint(IQuerySession session, IOptions<BookingOptions> options) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Get("/api/public/availability");
        AllowAnonymous();
        Options(x => x.RequireRateLimiting(BookingOptions.RateLimitPolicy));
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var settings = options.Value;

        if (!Guid.TryParse(req.Resource, out var id))
        {
            ThrowError("resource must be the id of a resource.", 400);
        }

        var from = Instant(req.From);
        var to = Instant(req.To);
        if (from is null || to is null)
        {
            ThrowError("from and to must be dates and times with a UTC offset, such as 2026-10-20T00:00:00Z.", 400);
        }

        if (to <= from)
        {
            ThrowError("to must be after from.", 400);
        }

        if (to - from > TimeSpan.FromDays(settings.MaxRangeDays))
        {
            ThrowError($"The range may be at most {settings.MaxRangeDays} days.", 400);
        }

        var resource = await session.LoadAsync<BookingResource>(id, ct);
        if (resource is null || !resource.Active || !OpenSlots.TryTimeZone(resource.TimeZone, out var zone))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var held = await BookedStretches.HeldAsync(session, settings, id, null, ct);
        var (free, truncated) = OpenSlots.Free(resource, zone, held, from!.Value, to!.Value, DateTimeOffset.UtcNow, settings.MaxSlots);

        HttpContext.Response.Headers.CacheControl = "no-store";
        await Send.OkAsync(new Response(
            BookingContract.Version,
            id,
            resource.TimeZone,
            resource.SlotMinutes,
            truncated,
            free.Select(s => new FreeSlot(s.Start.ToOffset(zone.GetUtcOffset(s.Start)), s.End.ToOffset(zone.GetUtcOffset(s.End)))).ToList()), ct);
    }

    private static DateTimeOffset? Instant(string? text)
    {
        // A "+" in a query string arrives as a space, so a space in the offset is read as the plus.
        var value = text?.Trim().Replace(' ', '+');
        if (value is null || !WithOffset().IsMatch(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed.ToUniversalTime()
            : null;
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}:?\d{2})$", RegexOptions.IgnoreCase)]
    private static partial Regex WithOffset();
}
