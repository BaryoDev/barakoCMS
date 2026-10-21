namespace barakoCMS.Features.Workflows;

/// <summary>Reads the offset of a schedule trigger, such as <c>-24h</c> or <c>2d</c>.</summary>
internal static class ScheduleOffset
{
    /// <summary>The prefix of the trigger event a schedule workflow stores.</summary>
    internal const string EventPrefix = "schedule:";

    /// <summary>The trigger event stored on, and recorded on the runs of, the schedule workflow with this name.</summary>
    internal static string EventOf(string workflowName) => EventPrefix + workflowName;

    /// <summary>Whether <paramref name="text"/> is a signed whole number plus m, h or d, at most 365 days either way.</summary>
    public static bool TryParse(string? text, out TimeSpan offset)
    {
        offset = default;
        if (text is not { Length: >= 2 }) return false;

        var unit = text[^1];
        if (unit is not ('m' or 'h' or 'd')) return false;
        if (!int.TryParse(text[..^1], System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture, out var n)) return false;

        // Long arithmetic through the double ticks of TimeSpan.From* could overflow for a huge n, so
        // the size is checked on the number before a span is built.
        var days = unit switch { 'm' => n / 1440.0, 'h' => n / 24.0, _ => n };
        if (Math.Abs(days) > 365) return false;

        offset = unit switch { 'm' => TimeSpan.FromMinutes(n), 'h' => TimeSpan.FromHours(n), _ => TimeSpan.FromDays(n) };
        return true;
    }
}
