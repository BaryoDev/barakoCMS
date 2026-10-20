using FastEndpoints;
using FluentValidation;

namespace BarakoCMS.Booking.Features.Resources;

internal sealed class ResourceValidator : Validator<ResourceRequest>
{
    public const int MaxNameLength = 200;
    public const int MaxWindows = 50;
    public const int MaxExceptions = 400;
    public const int LongestSlotMinutes = 1440;

    public ResourceValidator()
    {
        RuleFor(r => r.Name).NotEmpty().MaximumLength(MaxNameLength);
        RuleFor(r => r.EntryType).MaximumLength(MaxNameLength);
        RuleFor(r => r.EntryType).NotEmpty().When(r => r.EntryId is not null)
            .WithMessage("EntryType is required when EntryId is set.");
        RuleFor(r => r.TimeZone).Must(z => OpenSlots.TryTimeZone(z, out _))
            .WithMessage("TimeZone must be an IANA time zone id, such as Asia/Manila.");
        RuleFor(r => r.SlotMinutes).InclusiveBetween(5, LongestSlotMinutes).When(r => r.SlotMinutes is not null);

        RuleFor(r => r.OpeningHours).NotNull().Must(h => h is null || h.Count <= MaxWindows)
            .WithMessage($"At most {MaxWindows} opening hour windows.");
        RuleFor(r => r.OpeningHours).Must(HoursAreSound!).When(r => r.OpeningHours is { Count: <= MaxWindows })
            .WithMessage("Each opening window needs a weekday name, a start and an end as HH:mm (24:00 ends the day), "
                         + "the end after the start, and windows on one day may not overlap.");

        RuleFor(r => r.Exceptions).NotNull().Must(e => e is null || e.Count <= MaxExceptions)
            .WithMessage($"At most {MaxExceptions} exceptions.");
        RuleFor(r => r.Exceptions).Must(ExceptionsAreSound!).When(r => r.Exceptions is { Count: <= MaxExceptions })
            .WithMessage("Each exception needs a date as yyyy-MM-dd, listed once, and, unless it is closed, "
                         + "windows of HH:mm that end after they start and do not overlap.");
    }

    private static bool Sound(IEnumerable<(string Start, string End)> windows)
    {
        var parsed = new List<(TimeSpan Start, TimeSpan End)>();
        foreach (var (start, end) in windows)
        {
            if (!OpenSlots.TryClock(start, out var s) || !OpenSlots.TryClock(end, out var e) || e <= s)
            {
                return false;
            }

            parsed.Add((s, e));
        }

        return parsed.OrderBy(p => p.Start).Zip(parsed.OrderBy(p => p.Start).Skip(1)).All(p => p.First.End <= p.Second.Start);
    }

    private static bool HoursAreSound(List<OpeningWindow> hours) =>
        hours.All(h => h is not null && IsWeekdayName(h.Day))
        && hours.GroupBy(h => Enum.Parse<DayOfWeek>(h.Day, true)).All(g => Sound(g.Select(h => (h.Start, h.End))));

    private static bool IsWeekdayName(string? day) =>
        day is not null && Enum.GetNames<DayOfWeek>().Any(n => string.Equals(n, day.Trim(), StringComparison.OrdinalIgnoreCase));

    private static bool ExceptionsAreSound(List<ResourceException> exceptions)
    {
        if (exceptions.Any(e => e is null || !OpenSlots.TryDate(e.Date, out _)))
        {
            return false;
        }

        return exceptions.Select(e => e.Date).Distinct().Count() == exceptions.Count
            && exceptions.All(e => e.Closed || (e.Hours ?? []).All(h => h is not null) && Sound((e.Hours ?? []).Select(h => (h.Start, h.End))));
    }
}
