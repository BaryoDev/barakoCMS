namespace barakoCMS.Core.Interfaces;

/// <summary>
/// A content write would leave two entries holding the values a uniqueness rule of the type allows
/// one entry to hold.
/// </summary>
/// <remarks>
/// Raised by <see cref="IContentWriter"/> before the write is committed. On a create nothing has
/// been staged. On a change to a stored entry the writer has already ejected the session's pending
/// changes, the ones staged before the refused write included, so a caller that catches this and
/// saves commits nothing of it.
///
/// The message names the rule and the type and nothing else: not the values, and not the entry
/// holding them, which the caller may have no right to read. Endpoints answer it with 409.
/// </remarks>
public sealed class ContentUniquenessException : Exception
{
    public ContentUniquenessException(string contentType, string rule, string? whenState)
        : this(contentType, rule, whenState, inProgress: false, whenState is null
            ? $"The rule '{rule}' on '{contentType}' allows one entry per value, and another entry already holds this one."
            : $"The rule '{rule}' on '{contentType}' allows one entry per value while {whenState}, and another entry already holds this one.")
    {
    }

    private ContentUniquenessException(string contentType, string rule, string? whenState, bool inProgress, string message)
        : base(message)
    {
        ContentType = contentType;
        Rule = rule;
        WhenState = whenState;
        IsInProgress = inProgress;
    }

    /// <summary>
    /// Another write of the same values under the rule has not committed, and did not within the
    /// time the writer waits. Whether it will hold them is not known yet, so the write may be sent
    /// again.
    /// </summary>
    public static ContentUniquenessException InProgress(string contentType, string rule, string? whenState) =>
        new(contentType, rule, whenState, inProgress: true,
            $"Another write of the same values under the rule '{rule}' on '{contentType}' has not finished. Try again shortly.");

    /// <summary>The content type, as its definition names it.</summary>
    public string ContentType { get; }

    /// <summary>The rule's name, as the type declares it.</summary>
    public string Rule { get; }

    /// <summary>The lifecycle state the rule counts, or null when it counts every entry.</summary>
    public string? WhenState { get; }

    /// <summary>
    /// True when the values were not found held but another write of them was still in flight, so
    /// sending the write again may succeed.
    /// </summary>
    public bool IsInProgress { get; }
}
