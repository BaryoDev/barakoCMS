using barakoCMS.Models;

namespace barakoCMS.Features.Activity.List;

internal sealed class ListRequest : PaginatedRequest
{
    /// <summary>
    /// A cursor from an earlier response, or a timestamp. Left out, the newest rows come first and
    /// the response carries the cursor to poll from. Given, the rows after it come oldest first.
    /// Only <c>pageSize</c> applies; <c>page</c> is ignored, since a cursor is what moves.
    /// </summary>
    public string? Since { get; set; }

    /// <summary>
    /// Comma separated kinds to keep. Left out, every kind. A name this version does not know is
    /// ignored, so a client can send a kind a later version adds.
    /// </summary>
    public string? Kinds { get; set; }
}

/// <summary>Who did it. <c>kind</c> is <c>user</c> or <c>system</c>.</summary>
internal sealed class ActivityActor
{
    public string Kind { get; set; } = "system";
    public string Name { get; set; } = "System";
}

/// <summary>What it was done to. <c>kind</c> names the sort of thing and <c>title</c> is its label.</summary>
internal sealed class ActivitySubject
{
    public string Kind { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
}

/// <summary>One row of the feed.</summary>
internal sealed class ActivityRow
{
    /// <summary>
    /// Stable for the thing the row is about. A workflow run keeps its id as it moves from one kind
    /// to another, so a client replaces by id.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    public DateTimeOffset At { get; set; }

    /// <summary>One of the documented kinds. A client renders a name it does not know as it is.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary><c>info</c>, <c>warning</c> or <c>error</c>.</summary>
    public string Severity { get; set; } = ActivityKinds.Info;

    public ActivityActor Actor { get; set; } = new();
    public ActivitySubject Subject { get; set; } = new();

    /// <summary>A plain sentence, never markup and never carrying the subject's title.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>The console path to open the thing, relative to the console's root.</summary>
    public string Ref { get; set; } = string.Empty;
}

// The page fields are declared here and not inherited, as the audit list does, so a contract diff
// does not read an allOf as every field removed.
/// <summary>A page of activity.</summary>
internal sealed class ActivityResponse
{
    public IReadOnlyList<ActivityRow> Items { get; set; } = Array.Empty<ActivityRow>();

    /// <summary>Pass back as <c>since</c> to read what follows this response.</summary>
    public string Cursor { get; set; } = string.Empty;

    /// <summary>Whether more rows were already there after this page, so the next poll should not wait.</summary>
    public bool HasMore { get; set; }
}
