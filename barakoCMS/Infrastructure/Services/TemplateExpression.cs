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

/// <summary>The absolute bases a <c>links.</c> placeholder is built on. Each is null when it is not configured.</summary>
/// <param name="Api">This deployment's public URL (<c>App:BaseUrl</c>).</param>
/// <param name="Console">The console's URL (<c>App:ConsoleUrl</c>).</param>
/// <param name="Site">The <c>Url</c> of the tenant's published <c>site</c> entry.</param>
internal sealed record TemplateLinks(string? Api, string? Console, string? Site)
{
    public static readonly TemplateLinks None = new(null, null, null);
}

/// <summary>What one reference field of the entry points at, as the triggering user may read it.</summary>
/// <param name="Multiple">Whether the field holds a list of ids.</param>
/// <param name="Total">How many ids the field holds.</param>
/// <param name="Items">
/// The entries the user may read, in the field's order, taken from its first
/// <see cref="TemplateExpression.MaxLoopItems"/> ids only. Each carries only the fields the user is
/// shown. An entry that is missing, or that the user may not read, is not here.
/// </param>
internal sealed record TemplateFollowed(bool Multiple, int Total, IReadOnlyList<Content> Items);

/// <summary>What a template may read beyond the entry itself.</summary>
/// <param name="TimeZone">The zone a date is shown in. Null when the site names one this server does not know.</param>
/// <param name="Currency">The site's three-letter currency code, or null.</param>
/// <param name="Author">Who created the entry. Null when it was not loaded.</param>
/// <param name="Transition">Null unless the trigger is a transition whose event was found.</param>
/// <param name="Links">Null when nothing was read to build links, which leaves them as written.</param>
/// <param name="References">
/// The reference fields the templates follow, by field name. Null when none were read, which leaves
/// a <c>data.Field.Other</c> and a loop as written.
/// </param>
/// <param name="Notes">Where a resolve records what it left out on purpose. Null records nothing.</param>
internal sealed record TemplateContext(
    TimeZoneInfo? TimeZone,
    string? Currency,
    TemplatePerson? Author,
    TemplateTransition? Transition,
    TemplateLinks? Links = null,
    IReadOnlyDictionary<string, TemplateFollowed>? References = null,
    List<string>? Notes = null)
{
    /// <summary>
    /// For a caller that resolves without preparing. Dates are UTC, money carries no code, and the
    /// author, transition, link and reference placeholders are left as written, since nothing was
    /// read to fill them.
    /// </summary>
    public static readonly TemplateContext Unprepared = new(TimeZoneInfo.Utc, null, null, null);

    /// <summary>The context one loop item resolves in: no author, and no reference to follow further.</summary>
    public TemplateContext ForItem() => this with { Author = null, References = null };

    public void Note(string note)
    {
        if (Notes is not null && !Notes.Contains(note)) Notes.Add(note);
    }
}

/// <summary>What is inside one <c>{{...}}</c>: a variable, a variable with a format, a duration or a link.</summary>
/// <remarks>
/// Four shapes and nothing else, so there is no expression language to bound: <c>name</c>,
/// <c>name | format "argument"</c>, <c>duration from to</c> and <c>links.site "/path"</c>. The one
/// block is a loop, <c>{{#each data.Field}}...{{/each}}</c>, and it does not nest. A hole that is
/// none of them, or that names something unknown, is left as written. The plain shape is the one the engine
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

    /// <summary>The most entries one loop renders. Past it the loop stops and the run says so.</summary>
    public const int MaxLoopItems = 50;

    /// <summary>The longest path <c>links.site</c> takes, and the longest name <c>links.transition</c> takes.</summary>
    public const int MaxLinkArgumentLength = 200;

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

    /// <summary>
    /// A loop, or else one <see cref="Token"/>. A resolve is one pass of this, so a loop's body is
    /// template text and a value substituted into it is never read again.
    /// </summary>
    /// <remarks>
    /// The second alternative is <see cref="Token"/> itself, so a template with no loop is matched
    /// exactly as before.
    /// </remarks>
    public static readonly Regex Block = new(
        @"\{\{\s*\#each\s+data\.(?<field>[A-Za-z0-9_]+)\s*\}\}(?<body>.*?)\{\{\s*/each\s*\}\}|\{\{(?<hole>[^{}]*)\}\}",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex LoopOpen = new(@"\A\s*\#each\s+data\.[A-Za-z0-9_]+\s*\z", RegexOptions.Compiled);

    private static readonly Regex LoopClose = new(@"\A\s*/each\s*\z", RegexOptions.Compiled);

    private static readonly Regex NestedLoop = new(@"\{\{\s*\#each\b", RegexOptions.Compiled);

    private static readonly Regex Follows = new(
        @"\#each\s+data\.(?<name>[A-Za-z0-9_]+)|data\.(?<name>[A-Za-z0-9_]+)\.[A-Za-z0-9_]", RegexOptions.Compiled);

    private static readonly Regex Link = new(
        @"\A\s*(?<name>links\.(?:site|transition))\s+""(?<argument>[^""\r\n]*)""\s*\z", RegexOptions.Compiled);

    /// <summary>A path on the site: one leading slash, never two, so it cannot name another host.</summary>
    private static readonly Regex SitePath = new(@"\A/(?!/)[A-Za-z0-9\-._~/%?=&#+]*\z", RegexOptions.Compiled);

    private static readonly Regex Plain = new(@"\A\s*([A-Za-z0-9_.]+)\s*\z", RegexOptions.Compiled);

    private static readonly Regex Filtered = new(
        @"\A\s*(?<name>[A-Za-z0-9_.]+)\s*\|\s*(?<format>[A-Za-z]+)(?:\s+""(?<argument>[^""\r\n]*)"")*\s*\z",
        RegexOptions.Compiled);

    private static readonly Regex Between = new(
        @"\A\s*(?<function>duration|hours)\s+(?<from>[A-Za-z0-9_.]+)\s+(?<to>[A-Za-z0-9_.]+)\s*\z",
        RegexOptions.Compiled);

    private static readonly Regex ZoneId = new(
        @"\A[A-Za-z][A-Za-z0-9_+\-]*(?:/[A-Za-z0-9_+\-]+){0,2}\z", RegexOptions.Compiled);

    private static readonly Regex SiteFormat = new(
        @"(?:\||\\u007[cC])(?:\s|\\+[ntr])*(?:date|money)\b|links\.site\b", RegexOptions.Compiled);

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

    private static readonly HashSet<string> LinkNames = new(StringComparer.Ordinal)
    {
        "links.entry", "links.console", "links.edit",
    };

    private static readonly HashSet<string> TransitionNames = new(StringComparer.Ordinal)
    {
        "transition.name", "transition.at", "transition.by.name", "transition.by.email",
    };

    private enum Shape { Plain, Filtered, Between, Link }

    private sealed record Hole(Shape Shape, string Name, string Word, string Second, IReadOnlyList<string> Arguments);

    /// <summary>A date the entry or the transition carries, as opposed to a field that happens to hold one.</summary>
    /// <remarks>Written out round-trip when it has no format, which is what the engine has always done.</remarks>
    private readonly record struct Moment(DateTime Value);

    /// <summary>The text a hole stands for, before encoding, or null when it is left as written.</summary>
    public static string? Evaluate(string body, Content content, TemplateContext context)
    {
        if (Parse(body) is not { } hole) return null;

        if (hole.Shape == Shape.Link) return LinkTo(hole.Name, hole.Arguments[0], content, context);

        if (!TryValue(hole.Name, content, context, out var value))
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
    /// The entries a loop over a reference field renders, or null when the loop is left as written:
    /// the field is not a reference the context followed, or the body holds another loop.
    /// </summary>
    /// <remarks>
    /// Past <see cref="MaxLoopItems"/> ids the loop renders the entries among the first ones and
    /// stops, and the context is given a note saying how many the field held.
    /// </remarks>
    public static IReadOnlyList<Content>? LoopItems(string field, string body, TemplateContext context)
    {
        if (context.References is null
            || !context.References.TryGetValue(field, out var followed)
            || NestedLoop.IsMatch(body))
        {
            return null;
        }

        if (followed.Total > MaxLoopItems)
        {
            context.Note(string.Create(
                CultureInfo.InvariantCulture,
                $"The loop over data.{field} rendered from the first {MaxLoopItems} of its {followed.Total} references and stopped there."));
        }

        return followed.Items;
    }

    /// <summary>The reference fields a set of templates follows or loops over, by name.</summary>
    /// <remarks>By pattern, like <see cref="Needs"/>, so a Conditional's children are counted too.</remarks>
    public static HashSet<string> FollowedFields(IEnumerable<string?> templates)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var template in templates)
        {
            if (string.IsNullOrEmpty(template)) continue;

            foreach (Match match in Follows.Matches(template))
            {
                names.Add(match.Groups["name"].Value);
            }
        }

        return names;
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
            var problem = LoopOpen.IsMatch(match.Groups[1].Value) || LoopClose.IsMatch(match.Groups[1].Value)
                ? null
                : Problem(match.Groups[1].Value, onTransition);
            if (problem is null) continue;

            var shown = match.Value.Length <= 80 ? match.Value : match.Value[..80] + "...";
            yield return $"'{shown}' {problem}, so it is sent as written.";
        }

        foreach (Match match in Block.Matches(template))
        {
            if (match.Groups["field"].Success && NestedLoop.IsMatch(match.Groups["body"].Value))
            {
                yield return $"The loop over 'data.{match.Groups["field"].Value}' holds another loop, and loops do not nest, so it is sent as written.";
            }
        }
    }

    /// <summary>Whether a template holds a placeholder that resolves to a user's email address.</summary>
    public static bool NamesAddress(string? template) =>
        !string.IsNullOrEmpty(template)
        && Token.Matches(template).Any(match =>
            Parse(match.Groups[1].Value) is { } hole && (IsAddress(hole.Name) || IsAddress(hole.Second)));

    private static bool IsAddress(string name) => name is "createdBy.email" or "transition.by.email";

    /// <summary>Which reads a set of templates needs before it can be resolved.</summary>
    /// <remarks>
    /// By pattern and not by parsing, so the children a Conditional carries as JSON in one of its
    /// parameters are counted too. The site is read for a bar followed by <c>date</c> or
    /// <c>money</c>, with the bar and the white space after it also accepted as JSON wrote them
    /// (<c>|</c>, <c>\n</c>). A read too few would format a date in UTC for a site that is
    /// not in UTC, and the words alone would read the site for every "updated" and "candidate".
    /// </remarks>
    public static (bool Site, bool Author, bool Transition) Needs(IEnumerable<string?> templates)
    {
        var site = false;
        var author = false;
        var transition = false;

        foreach (var template in templates)
        {
            if (string.IsNullOrEmpty(template)) continue;

            site |= SiteFormat.IsMatch(template);
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

        var link = Link.Match(body);
        if (link.Success)
        {
            return new Hole(Shape.Link, link.Groups["name"].Value, string.Empty, string.Empty, [link.Groups["argument"].Value]);
        }

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

        if (hole.Shape == Shape.Link) return LinkArgumentProblem(hole.Name, hole.Arguments[0]);

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
        if (EntryNames.Contains(name) || LinkNames.Contains(name) || name == "links.site" || IsField(name)) return null;

        if (name == "links.transition") return "names 'links.transition' without the transition, as in links.transition \"Approve\"";

        if (TransitionNames.Contains(name))
        {
            return onTransition ? null : $"names '{name}', which is only filled when the trigger is a transition";
        }

        return $"names '{name}', which is not a placeholder";
    }

    private static bool IsField(string name) => name.Length > 5 && name.StartsWith("data.", StringComparison.Ordinal);

    private static bool CanBeDate(string name) =>
        name is "createdAt" or "updatedAt" or "transition.at" || IsField(name);

    /// <summary>What is wrong with a link's argument, or null. Evaluation refuses on the same answer.</summary>
    private static string? LinkArgumentProblem(string name, string argument)
    {
        if (argument.Length is 0 or > MaxLinkArgumentLength)
        {
            return $"gives '{name}' an argument that is not between 1 and {MaxLinkArgumentLength} characters";
        }

        if (name == "links.site")
        {
            return SitePath.IsMatch(argument)
                ? null
                : "gives 'links.site' a path that does not start with one '/' or holds a character a path does not";
        }

        return argument.Any(char.IsControl) ? "gives 'links.transition' a name with a control character in it" : null;
    }

    /// <summary>
    /// A link to a page of the site or to the entry in the console with a transition named. Empty
    /// when the base it needs is not configured, and left as written when nothing was prepared.
    /// </summary>
    private static string? LinkTo(string name, string argument, Content content, TemplateContext context)
    {
        if (context.Links is not { } links || LinkArgumentProblem(name, argument) is not null) return null;

        if (name == "links.site") return links.Site is null ? string.Empty : links.Site + argument;

        if (links.Console is null || content is barakoCMS.Features.Workflows.ErasedContent) return string.Empty;

        return $"{links.Console}/content/{content.Id}?transition={Uri.EscapeDataString(argument)}";
    }

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

        if (key.StartsWith("links.", StringComparison.Ordinal))
        {
            return TryLink(key, content, context, out value);
        }

        if (key.StartsWith("data.", StringComparison.Ordinal) && content.Data != null)
        {
            var name = key.Substring("data.".Length);
            return content.Data.TryGetValue(name, out value) || TryFollow(name, context, out value);
        }

        return false;
    }

    /// <summary>
    /// <c>Field.Other</c> where <c>Field</c> is a single reference the context followed: the
    /// referenced entry's <c>Other</c>, or nothing.
    /// </summary>
    /// <remarks>
    /// Nothing, and not the placeholder as written, whenever there is no value to give: no entry,
    /// one the triggering user may not read, a field they are not shown, a field it does not have.
    /// Telling those apart would tell the reader of the message which of them it was. A field
    /// literally named with a dot is read first, as it always was.
    /// </remarks>
    private static bool TryFollow(string name, TemplateContext context, out object? value)
    {
        value = null;

        var dot = name.IndexOf('.');
        if (dot <= 0 || context.References is null || !context.References.TryGetValue(name[..dot], out var followed))
        {
            return false;
        }

        if (!followed.Multiple && followed.Items.Count > 0
            && followed.Items[0].Data.TryGetValue(name[(dot + 1)..], out var found))
        {
            value = found;
        }

        return true;
    }

    private static bool TryLink(string key, Content content, TemplateContext context, out object? value)
    {
        value = null;
        if (context.Links is not { } links) return false;

        var erased = content is barakoCMS.Features.Workflows.ErasedContent;

        switch (key)
        {
            case "links.entry":
                value = links.Api is null || erased ? string.Empty : $"{links.Api}/api/contents/{content.Id}";
                return true;

            case "links.console" or "links.edit":
                value = links.Console is null || erased ? string.Empty : $"{links.Console}/content/{content.Id}";
                return true;

            case "links.site":
                value = links.Site ?? string.Empty;
                return true;

            default:
                return false;
        }
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
