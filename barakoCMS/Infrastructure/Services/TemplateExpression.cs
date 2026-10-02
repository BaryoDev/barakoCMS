using System.Globalization;
using System.Text.RegularExpressions;
using barakoCMS.Models;

namespace barakoCMS.Infrastructure.Services;

/// <summary>A user as a template may name one: the username and the address.</summary>
internal sealed record TemplatePerson(string Name, string Email)
{
    /// <summary>What a change with no user behind it resolves to: a scheduled publish, an import, a deleted account.</summary>
    public static readonly TemplatePerson Nobody = new(string.Empty, string.Empty);

    /// <summary>What a simulation shows in place of a real user.</summary>
    public static readonly TemplatePerson Sample = new("sample.user", "sample.user@example.com");
}

/// <summary>The lifecycle transition that fired the workflow.</summary>
internal sealed record TemplateTransition(string Name, DateTime At, TemplatePerson By);

/// <summary>What a template may read beyond the entry itself.</summary>
/// <param name="TimeZone">The zone a date is shown in. Null when the site names one this server does not know.</param>
/// <param name="Currency">The site's three-letter currency code, or null.</param>
/// <param name="Author">Who created the entry. Null when it was not loaded.</param>
/// <param name="Transition">Null unless the trigger is a transition whose event was found.</param>
internal sealed record TemplateContext(
    TimeZoneInfo? TimeZone, string? Currency, TemplatePerson? Author, TemplateTransition? Transition)
{
    /// <summary>
    /// For a caller that resolves without preparing. Dates are UTC, money carries no code, and the
    /// author and transition placeholders are left as written, since nothing was read to fill them.
    /// </summary>
    public static readonly TemplateContext Unprepared = new(TimeZoneInfo.Utc, null, null, null);
}

/// <summary>What is inside one <c>{{...}}</c>: a variable, a variable with a format, or a duration.</summary>
/// <remarks>
/// Three shapes and nothing else, so there is no expression language to bound:
/// <c>name</c>, <c>name | format "argument"</c> and <c>duration from to</c>. A hole that is none of
/// them, or that names something unknown, is left as written. The plain shape is the one the engine
/// has always read, and it is tried first with the same pattern, so a template that resolved before
/// formats existed resolves to the same text.
///
/// Every culture here is the invariant one. The server's culture would make the same template read
/// differently on two hosts.
/// </remarks>
internal static class TemplateExpression
{
    /// <summary>Past this, a hole with a format or a duration is left as written.</summary>
    public const int MaxLength = 256;

    /// <summary>The longest date format string accepted.</summary>
    public const int MaxFormatLength = 64;

    public const string DefaultDateFormat = "yyyy-MM-dd HH:mm";

    public static readonly IReadOnlyList<string> Formats = ["date", "money", "upper", "lower"];

    /// <summary>
    /// Every <c>{{...}}</c> with no brace inside it.
    /// </summary>
    /// <remarks>
    /// Wider than the pattern it replaces, which only matched a variable name. Each match of that
    /// pattern is a match of this one over the same characters, since neither lets a brace inside,
    /// so what used to resolve is found where it was. What is new is found too, and left as written
    /// unless it is one of the three shapes.
    /// </remarks>
    public static readonly Regex Token = new(@"\{\{([^{}]*)\}\}", RegexOptions.Compiled);

    private static readonly Regex Plain = new(@"\A\s*([A-Za-z0-9_.]+)\s*\z", RegexOptions.Compiled);

    private static readonly Regex Filtered = new(
        @"\A\s*(?<name>[A-Za-z0-9_.]+)\s*\|\s*(?<format>[A-Za-z]+)(?:\s+""(?<argument>[^""\r\n]*)"")*\s*\z",
        RegexOptions.Compiled);

    private static readonly Regex Between = new(
        @"\A\s*(?<function>duration|hours)\s+(?<from>[A-Za-z0-9_.]+)\s+(?<to>[A-Za-z0-9_.]+)\s*\z",
        RegexOptions.Compiled);

    private static readonly Regex ZoneId = new(
        @"\A[A-Za-z][A-Za-z0-9_+\-]*(?:/[A-Za-z0-9_+\-]+){0,2}\z", RegexOptions.Compiled);

    private static readonly string[] DateFormats =
    [
        "yyyy-MM-dd",
        "yyyy-MM-dd'T'HH:mmK",
        "yyyy-MM-dd'T'HH:mm:ssK",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
    ];

    private static readonly HashSet<string> EntryNames = new(StringComparer.Ordinal)
    {
        "id", "contentType", "status", "createdAt", "updatedAt", "createdBy.name", "createdBy.email",
    };

    private static readonly HashSet<string> TransitionNames = new(StringComparer.Ordinal)
    {
        "transition.name", "transition.at", "transition.by.name", "transition.by.email",
    };

    private enum Shape { Plain, Filtered, Between }

    private sealed record Hole(Shape Shape, string Name, string Word, string Second, IReadOnlyList<string> Arguments);

    /// <summary>A date the entry or the transition carries, as opposed to a field that happens to hold one.</summary>
    /// <remarks>Written out round-trip when it has no format, which is what the engine has always done.</remarks>
    private readonly record struct Moment(DateTime Value);

    /// <summary>The text a hole stands for, before encoding, or null when it is left as written.</summary>
    public static string? Evaluate(string body, Content content, TemplateContext context)
    {
        if (Parse(body) is not { } hole || !TryValue(hole.Name, content, context, out var value))
        {
            return null;
        }

        switch (hole.Shape)
        {
            case Shape.Plain:
                return Text(value);

            case Shape.Between:
                return TryValue(hole.Second, content, context, out var end) ? Span(hole.Word, value, end) : null;

            default:
                if (ArgumentProblem(hole.Word, hole.Arguments) is not null) return null;

                return hole.Word switch
                {
                    "date" => Date(value, hole.Arguments, context),
                    "money" => Money(value, hole.Arguments, context),
                    "upper" => Text(value).ToUpperInvariant(),
                    "lower" => Text(value).ToLowerInvariant(),
                    _ => null,
                };
        }
    }

    /// <summary>
    /// What the engine will leave as written in a template, as far as the template alone can say.
    /// </summary>
    /// <remarks>
    /// A <c>data.</c> field is never reported: whether an entry has it is only known when one
    /// arrives. The hole is quoted back, cut short, to the caller who sent it and nowhere else.
    /// </remarks>
    public static IEnumerable<string> Problems(string? template, bool onTransition)
    {
        if (string.IsNullOrEmpty(template)) yield break;

        foreach (Match match in Token.Matches(template))
        {
            var problem = Problem(match.Groups[1].Value, onTransition);
            if (problem is null) continue;

            var shown = match.Value.Length <= 80 ? match.Value : match.Value[..80] + "...";
            yield return $"'{shown}' {problem}, so it is sent as written.";
        }
    }

    /// <summary>Which reads a set of templates needs before it can be resolved.</summary>
    /// <remarks>
    /// By word and not by parsing, so the children a Conditional carries as JSON in one of its
    /// parameters are counted too, however that JSON escaped its braces, bars and quotes. A read too
    /// many is one query. A read too few would format a date in UTC for a site that is not in UTC.
    /// </remarks>
    public static (bool Site, bool Author, bool Transition) Needs(IEnumerable<string?> templates)
    {
        var site = false;
        var author = false;
        var transition = false;

        foreach (var template in templates)
        {
            if (string.IsNullOrEmpty(template)) continue;

            site |= template.Contains("date", StringComparison.Ordinal) || template.Contains("money", StringComparison.Ordinal);
            author |= template.Contains("createdBy.", StringComparison.Ordinal);
            transition |= template.Contains("transition.", StringComparison.Ordinal);
        }

        return (site, author, transition);
    }

    /// <summary>The time zone an id names, or null when it is not one this server knows.</summary>
    /// <remarks>
    /// The shape is checked before the lookup, because on Linux the lookup opens a file named after
    /// the id.
    /// </remarks>
    public static TimeZoneInfo? Zone(string? id) =>
        id is { Length: > 0 and <= 64 } && ZoneId.IsMatch(id) && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone)
            ? zone
            : null;

    /// <summary>A three-letter code in capitals, or null for anything else.</summary>
    public static string? CurrencyCode(string? value) =>
        value is { Length: 3 } && value.All(char.IsAsciiLetter) ? value.ToUpperInvariant() : null;

    private static Hole? Parse(string body)
    {
        var plain = Plain.Match(body);
        if (plain.Success) return new Hole(Shape.Plain, plain.Groups[1].Value, string.Empty, string.Empty, []);

        if (body.Length > MaxLength) return null;

        var between = Between.Match(body);
        if (between.Success)
        {
            return new Hole(
                Shape.Between, between.Groups["from"].Value, between.Groups["function"].Value, between.Groups["to"].Value, []);
        }

        var filtered = Filtered.Match(body);
        if (filtered.Success)
        {
            return new Hole(
                Shape.Filtered, filtered.Groups["name"].Value, filtered.Groups["format"].Value, string.Empty,
                filtered.Groups["argument"].Captures.Select(capture => capture.Value).ToList());
        }

        return null;
    }

    private static string? Problem(string body, bool onTransition)
    {
        if (Parse(body) is not { } hole) return "is not a placeholder";

        if (NameProblem(hole.Name, onTransition) is { } name) return name;

        switch (hole.Shape)
        {
            case Shape.Plain:
                return null;

            case Shape.Between:
                if (NameProblem(hole.Second, onTransition) is { } second) return second;
                return CanBeDate(hole.Name) && CanBeDate(hole.Second)
                    ? null
                    : $"asks '{hole.Word}' for the time between two values that are not both dates";

            default:
                if (ArgumentProblem(hole.Word, hole.Arguments) is { } argument) return argument;
                if (hole.Word == "date" && !CanBeDate(hole.Name)) return $"formats '{hole.Name}' as a date, which it is not";
                if (hole.Word == "money" && !IsField(hole.Name)) return $"formats '{hole.Name}' as money, and it is not a number";
                return null;
        }
    }

    private static string? NameProblem(string name, bool onTransition)
    {
        if (EntryNames.Contains(name) || IsField(name)) return null;

        if (TransitionNames.Contains(name))
        {
            return onTransition ? null : $"names '{name}', which is only filled when the trigger is a transition";
        }

        return $"names '{name}', which is not a placeholder";
    }

    private static bool IsField(string name) => name.Length > 5 && name.StartsWith("data.", StringComparison.Ordinal);

    private static bool CanBeDate(string name) =>
        name is "createdAt" or "updatedAt" or "transition.at" || IsField(name);

    /// <summary>What is wrong with a format and its arguments, or null. Evaluation refuses on the same answer.</summary>
    private static string? ArgumentProblem(string format, IReadOnlyList<string> arguments)
    {
        switch (format)
        {
            case "date":
                if (arguments.Count > 2) return "gives 'date' more than a format and a time zone";
                if (arguments.Count > 0 && arguments[0].Length is 0 or > MaxFormatLength)
                {
                    return $"has a date format that is not between 1 and {MaxFormatLength} characters";
                }

                return arguments.Count == 2 && Zone(arguments[1]) is null
                    ? $"names the time zone '{arguments[1]}', which this server does not know"
                    : null;

            case "money":
                if (arguments.Count > 1) return "gives 'money' more than a currency code";
                return arguments.Count == 1 && CurrencyCode(arguments[0]) is null
                    ? "has a currency code that is not three letters"
                    : null;

            case "upper" or "lower":
                return arguments.Count == 0 ? null : $"gives '{format}' an argument, and it takes none";

            default:
                return $"uses '{format}', which is not a format (the formats are {string.Join(", ", Formats)})";
        }
    }

    /// <summary>The value a name stands for. False when the name is not a known variable.</summary>
    /// <remarks>A null or empty value is a known variable with nothing in it, and resolves to nothing.</remarks>
    private static bool TryValue(string key, Content content, TemplateContext context, out object? value)
    {
        value = null;

        // An erased entry has no status or timestamps to report. Empty, not the defaults of a new
        // Content, which would read as Draft and the time the action ran.
        if (content is barakoCMS.Features.Workflows.ErasedContent && key is "status" or "createdAt" or "updatedAt")
        {
            return true;
        }

        switch (key)
        {
            case "id": value = content.Id.ToString(); return true;
            case "contentType": value = content.ContentType; return true;
            case "status": value = content.Status.ToString(); return true;
            case "createdAt": value = new Moment(content.CreatedAt); return true;
            case "updatedAt": value = new Moment(content.UpdatedAt); return true;

            case "createdBy.name": value = context.Author?.Name; return context.Author is not null;
            case "createdBy.email": value = context.Author?.Email; return context.Author is not null;

            case "transition.name": value = context.Transition?.Name; return context.Transition is not null;
            case "transition.by.name": value = context.Transition?.By.Name; return context.Transition is not null;
            case "transition.by.email": value = context.Transition?.By.Email; return context.Transition is not null;
            case "transition.at":
                if (context.Transition is null) return false;
                value = new Moment(context.Transition.At);
                return true;
        }

        if (key.StartsWith("data.", StringComparison.Ordinal) && content.Data != null)
        {
            return content.Data.TryGetValue(key.Substring("data.".Length), out value);
        }

        return false;
    }

    private static string Text(object? value) => value switch
    {
        null => string.Empty,
        Moment moment => moment.Value.ToString("o"),
        _ => value.ToString() ?? string.Empty,
    };

    private static bool IsEmpty(object? value) => value is null or "";

    private static string? Date(object? value, IReadOnlyList<string> arguments, TemplateContext context)
    {
        var zone = arguments.Count == 2 ? Zone(arguments[1]) : context.TimeZone;
        if (zone is null) return null;

        if (IsEmpty(value)) return string.Empty;
        if (!TryMoment(value, out var moment)) return null;

        try
        {
            return TimeZoneInfo.ConvertTime(moment, zone)
                .ToString(arguments.Count > 0 ? arguments[0] : DefaultDateFormat, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            // A format .NET refuses, or a date too close to year one to move into the zone.
            return null;
        }
    }

    /// <summary>Two decimals with thousands separators, after the currency code when there is one.</summary>
    private static string? Money(object? value, IReadOnlyList<string> arguments, TemplateContext context)
    {
        if (IsEmpty(value)) return string.Empty;
        if (!TryNumber(value, out var amount)) return null;

        var code = arguments.Count == 1 ? CurrencyCode(arguments[0]) : context.Currency;
        var text = amount.ToString("N2", CultureInfo.InvariantCulture);

        return string.IsNullOrEmpty(code) ? text : $"{code} {text}";
    }

    /// <summary>
    /// The time between two dates: "8 hours 30 minutes" for <c>duration</c>, "8.5" for <c>hours</c>.
    /// </summary>
    /// <remarks>
    /// Counted in whole ticks, so no rounding depends on a floating point product. <c>duration</c>
    /// drops the seconds and <c>hours</c> rounds to the nearest tenth, a half going up. An end
    /// before its start reads with a minus sign in front and is not hidden as zero.
    /// </remarks>
    private static string? Span(string function, object? from, object? to)
    {
        if (IsEmpty(from) || IsEmpty(to)) return string.Empty;
        if (!TryMoment(from, out var start) || !TryMoment(to, out var end)) return null;

        var ticks = (end - start).Ticks;
        var length = Math.Abs(ticks);

        if (function == "hours")
        {
            const long Tenth = TimeSpan.TicksPerHour / 10;
            var tenths = (length + (Tenth / 2)) / Tenth;
            var sign = ticks < 0 && tenths > 0 ? "-" : string.Empty;

            return string.Create(CultureInfo.InvariantCulture, $"{sign}{tenths / 10}.{tenths % 10}");
        }

        var minutes = length / TimeSpan.TicksPerMinute;
        var hours = minutes / 60;
        var rest = minutes % 60;

        var hoursText = string.Create(CultureInfo.InvariantCulture, $"{hours} {(hours == 1 ? "hour" : "hours")}");
        var minutesText = string.Create(CultureInfo.InvariantCulture, $"{rest} {(rest == 1 ? "minute" : "minutes")}");

        var text = hours == 0 ? minutesText : rest == 0 ? hoursText : $"{hoursText} {minutesText}";

        return ticks < 0 && minutes > 0 ? "-" + text : text;
    }

    /// <summary>Reads a value as an instant. A time with no zone on it is taken as UTC.</summary>
    /// <remarks>
    /// Text is read against a fixed list of ISO 8601 shapes. A general parse would read "1.5" as a
    /// day of the current year, so the same template would resolve differently next year.
    /// </remarks>
    private static bool TryMoment(object? value, out DateTimeOffset moment)
    {
        switch (value)
        {
            case Moment stamped:
                moment = Utc(stamped.Value);
                return true;

            case DateTime date:
                moment = Utc(date);
                return true;

            case DateTimeOffset offset:
                moment = offset;
                return true;

            default:
                return DateTimeOffset.TryParseExact(
                    value?.ToString()?.Trim(), DateFormats, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out moment);
        }
    }

    private static DateTimeOffset Utc(DateTime value) =>
        value.Kind == DateTimeKind.Unspecified
            ? new DateTimeOffset(value, TimeSpan.Zero)
            : new DateTimeOffset(value.ToUniversalTime(), TimeSpan.Zero);

    private static bool TryNumber(object? value, out decimal number)
    {
        number = 0;

        switch (value)
        {
            case decimal exact:
                number = exact;
                return true;

            case sbyte or byte or short or ushort or int or uint or long or ulong or float or double:
                try
                {
                    number = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                    return true;
                }
                catch (OverflowException)
                {
                    return false;
                }

            default:
                return decimal.TryParse(
                    value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number);
        }
    }
}
