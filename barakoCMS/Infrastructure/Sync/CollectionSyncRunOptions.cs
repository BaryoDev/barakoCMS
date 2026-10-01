using System.Globalization;

namespace barakoCMS.Infrastructure.Sync;

/// <summary>
/// How long a run started from the API waits for a collection another run is filling, read once at
/// startup.
/// </summary>
internal sealed class CollectionSyncRunOptions
{
    /// <summary>Seconds, 5 by default, held between 0 and 30. 0 answers 409 at once.</summary>
    public const string LockWaitKey = "CollectionSyncs:RunLockWaitSeconds";

    /// <remarks>
    /// Long enough for the run in the way to finish when it is one sync of a handful of entries: a
    /// fetch and a commit per changed entry. Short enough that a request is not held open for a
    /// sync of hundreds.
    /// </remarks>
    public static readonly TimeSpan DefaultLockWait = TimeSpan.FromSeconds(5);

    /// <summary>The longest a request is ever held open waiting, whatever is configured.</summary>
    public static readonly TimeSpan MaxLockWait = TimeSpan.FromSeconds(30);

    public TimeSpan LockWait { get; internal set; } = DefaultLockWait;

    /// <remarks>
    /// A value that is not a number stops the deployment starting. Read lazily it would start, and
    /// every run request would then fail on it.
    /// </remarks>
    public static CollectionSyncRunOptions FromConfiguration(IConfiguration configuration)
    {
        var raw = configuration[LockWaitKey];

        if (string.IsNullOrWhiteSpace(raw))
        {
            return new CollectionSyncRunOptions();
        }

        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            || !double.IsFinite(seconds))
        {
            throw new InvalidOperationException(
                $"{LockWaitKey} is '{raw}', which is not a number of seconds. Use a number from 0 to "
                + $"{MaxLockWait.TotalSeconds.ToString(CultureInfo.InvariantCulture)}, written with a point, or leave it unset for "
                + $"{DefaultLockWait.TotalSeconds.ToString(CultureInfo.InvariantCulture)}.");
        }

        return new CollectionSyncRunOptions
        {
            LockWait = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, MaxLockWait.TotalSeconds)),
        };
    }
}
