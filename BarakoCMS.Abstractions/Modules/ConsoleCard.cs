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
/// 503 or anything that is not a valid body.
/// </remarks>
public sealed record ConsoleCardContent(
    string State,
    string Text,
    IReadOnlyList<ConsoleCardRow> Rows,
    string? Note = null,
    DateTimeOffset? UpdatedAt = null);

/// <summary>One label and value line on a card.</summary>
public sealed record ConsoleCardRow(string Label, string Value);

/// <summary>The values <see cref="ConsoleCardContent.State"/> may take.</summary>
public static class ConsoleCardState
{
    public const string Ok = "ok";
    public const string Empty = "empty";
    public const string Unavailable = "unavailable";
}
