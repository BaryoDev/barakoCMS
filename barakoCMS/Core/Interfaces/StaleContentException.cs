namespace barakoCMS.Core.Interfaces;

/// <summary>
/// A write to an event-sourced content item was made against a read that the stream has moved past.
/// </summary>
/// <remarks>
/// Only event-sourced types raise this. Decision 3 of #230 puts them on expected-version checking
/// and leaves every other type on last-write-wins, because moving a type from last-write-wins to
/// expected-version later is a breaking change (clients start seeing a status they never handled)
/// and moving the other way breaks nothing. So two content types in one API answer the same request
/// differently, deliberately.
///
/// Endpoints map this to 409.
/// </remarks>
public sealed class StaleContentException : Exception
{
    public StaleContentException(Guid contentId, long? expectedVersion, long actualVersion)
        : base(expectedVersion is null
            ? $"Content {contentId} is event sourced, so a write has to say which version it was "
              + $"read at. The stream is at version {actualVersion}."
            : $"Content {contentId} was read at version {expectedVersion} and the stream is at "
              + $"version {actualVersion}. Refresh and try again.")
    {
        ContentId = contentId;
        ExpectedVersion = expectedVersion;
        ActualVersion = actualVersion;
    }

    public Guid ContentId { get; }

    /// <summary>The version the caller read at, or null when the caller did not say.</summary>
    public long? ExpectedVersion { get; }

    public long ActualVersion { get; }
}
