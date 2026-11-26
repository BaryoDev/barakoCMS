using System.Text.Json.Serialization;

namespace barakoCMS.Modules;

/// <summary>
/// A small card a module asks the console to draw, from a route the module serves itself.
/// </summary>
/// <param name="Id">
/// Lowercase letters, digits and hyphens, 1 to 40 characters, starting with a letter or digit.
/// Unique within the module.
/// </param>
/// <param name="Title">1 to 60 characters after trimming, no control or format characters.</param>
/// <param name="Route">
/// A path on this host the console calls with the caller's own credentials: <c>/api/</c> followed
/// by segments of letters, digits, hyphens and underscores, at most 200 characters. No query, no
/// <c>..</c>, no <c>//</c>, no scheme, no host, no backslash, and not under <c>/api/auth</c>. The route answers with a
/// <see cref="ConsoleCardContent"/> and enforces its own permission.
/// </param>
/// <param name="RefreshLabel">
/// Null means no button. Otherwise 1 to 24 characters after trimming, no control, format,
/// line separator or paragraph separator characters (the same rule as <paramref name="Title"/>). When set, the console shows a button with
/// this label. Clicking it sends POST to <paramref name="Route"/> with no body. The route answers like
/// its GET (a <see cref="ConsoleCardContent"/>), or 429 with Retry-After. A route that declares a
/// label must accept POST and enforce the same permission on it. The POST may do work the GET avoids,
/// since it is how a person asks for a fresh card.
/// </param>
/// <remarks>
/// Core checks these rules when it builds the module list. A card that breaks one is dropped and a
/// warning names the module and the card id. The module still runs. At most four cards per module
/// are kept. See <see cref="IBarakoModule.ConsoleCards"/>.
/// </remarks>
public sealed record ConsoleCard(string Id, string Title, string Route, string? RefreshLabel = null);

/// <summary>
/// What a card route returns. Core does not serve this type: a module returns it from its own route.
/// </summary>
/// <param name="State">One of <see cref="ConsoleCardState"/>.</param>
/// <param name="Text">A short sentence for the card body.</param>
/// <param name="Rows">Label and value pairs, shown in order.</param>
/// <param name="Note">A small line under the card, or null.</param>
/// <param name="UpdatedAt">When the figures were taken, or null.</param>
/// <remarks>
/// A console applies these caps and cuts anything longer: text 1000 characters, at most 12 rows,
/// label 60, value 120, note 200. A console hides the card when the route answers 401, 403, 404,
/// 503 or anything that is not a valid body. The visual parts are optional and additive. A console
/// that does not know them ignores them, and a card without parts draws as before. They are read
/// only when <paramref name="State"/> is <c>ok</c>. They are init properties so the positional
/// constructor stays as it was, and a part left null is not written to the JSON.
/// </remarks>
public sealed record ConsoleCardContent(
    string State,
    string Text,
    IReadOnlyList<ConsoleCardRow> Rows,
    string? Note = null,
    DateTimeOffset? UpdatedAt = null)
{
    /// <summary>Up to 4 stats are drawn. A stat that breaks a rule is dropped.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ConsoleCardStat>? Stats { get; init; }

    /// <summary>A line over days. Drawn only with 2 to 31 valid points.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ConsoleCardSeries? Series { get; init; }

    /// <summary>A bar list of up to 8 items.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ConsoleCardBars? Bars { get; init; }

    /// <summary>Up to 6 status lines.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ConsoleCardCheck>? Checks { get; init; }

    /// <summary>1 to 40 characters. Null or longer means "Details".</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RowsLabel { get; init; }

    /// <summary>A question box the card names.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ConsoleCardQuestion? Question { get; init; }
}

/// <summary>One figure on a card.</summary>
/// <param name="Label">1 to 40 characters.</param>
/// <param name="Value">Finite and 0 or more.</param>
/// <param name="Previous">The figure for the period before, finite and 0 or more, or null.</param>
/// <param name="Format"><c>count</c> or <c>percent</c>. Anything else drops the stat.</param>
/// <param name="Total">An overall figure to show beside the value, finite and 0 or more, or null.</param>
/// <param name="GoodWhen">
/// <c>up</c> or <c>down</c>: which direction of change is good news, or null for neither.
/// Anything else drops the stat.
/// </param>
/// <remarks>At most 4 stats are kept per card.</remarks>
public sealed record ConsoleCardStat(
    string Label,
    double Value,
    double? Previous = null,
    string Format = "count",
    double? Total = null,
    string? GoodWhen = null);

/// <summary>A line over days.</summary>
/// <param name="Label">1 to 60 characters.</param>
/// <param name="Points">2 to 31 points. More than 31 drops the series. Fewer than 2 valid points drops it too.</param>
/// <param name="PreviousLabel">
/// 1 to 60 characters, names the earlier line, or null. The earlier line is drawn only when every
/// point has a <see cref="ConsoleCardPoint.Previous"/>.
/// </param>
public sealed record ConsoleCardSeries(
    string Label,
    IReadOnlyList<ConsoleCardPoint> Points,
    string? PreviousLabel = null);

/// <summary>One day on a <see cref="ConsoleCardSeries"/>.</summary>
/// <param name="Date">A real calendar day as <c>YYYY-MM-DD</c>.</param>
/// <param name="Value">Finite and 0 or more.</param>
/// <param name="Previous">The matching figure for the earlier line, finite and 0 or more, or null.</param>
public sealed record ConsoleCardPoint(string Date, double Value, double? Previous = null);

/// <summary>A ranked bar list.</summary>
/// <param name="Label">1 to 60 characters.</param>
/// <param name="Items">At most 8 items are kept. A list with no valid item is dropped.</param>
public sealed record ConsoleCardBars(string Label, IReadOnlyList<ConsoleCardBar> Items);

/// <summary>One bar.</summary>
/// <param name="Label">1 to 60 characters.</param>
/// <param name="Value">Finite and 0 or more.</param>
public sealed record ConsoleCardBar(string Label, double Value);

/// <summary>One status line.</summary>
/// <param name="Label">1 to 60 characters.</param>
/// <param name="Tone"><c>ok</c>, <c>warn</c> or <c>bad</c>. Anything else drops the check.</param>
/// <param name="Value">A short detail of 1 to 120 characters, or null.</param>
/// <remarks>At most 6 checks are kept per card.</remarks>
public sealed record ConsoleCardCheck(string Label, string Tone, string? Value = null);

/// <summary>A question box on a card.</summary>
/// <param name="Route">
/// Held to the same rules as <see cref="ConsoleCard.Route"/>, and its first two segments (such as
/// <c>/api/weather</c>) must match the card route's. Otherwise the question box is dropped.
/// </param>
/// <param name="Placeholder">1 to 60 characters.</param>
/// <param name="MaxLength">1 to 1000, a whole number. Anything else drops the question box.</param>
/// <param name="MaxTurns">
/// 0 to 20, a whole number: how many earlier messages the console sends back, where a question and its answer are 2. 0 sends none.
/// Anything else drops the question box.
/// </param>
public sealed record ConsoleCardQuestion(
    string Route,
    string Placeholder,
    int MaxLength = 500,
    int MaxTurns = 10);

/// <summary>One label and value line on a card.</summary>
public sealed record ConsoleCardRow(string Label, string Value);

/// <summary>The values <see cref="ConsoleCardContent.State"/> may take.</summary>
public static class ConsoleCardState
{
    public const string Ok = "ok";
    public const string Empty = "empty";
    public const string Unavailable = "unavailable";
}
