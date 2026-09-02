namespace BarakoCMS.Files;

/// <summary>What a scanner concluded about a file.</summary>
public enum ScanVerdict
{
    /// <summary>Scanned, nothing found.</summary>
    Clean = 0,

    /// <summary>Scanned, something found. <see cref="ScanResult.Signature"/> names it.</summary>
    Infected = 1,

    /// <summary>
    /// Not scanned, because the scanner could not be reached or did not answer in time.
    /// </summary>
    /// <remarks>
    /// Deliberately not folded into <see cref="Clean"/>. "Nobody looked" and "somebody looked and
    /// found nothing" are different facts, and a scanner that fails open turns an outage into a
    /// silent delivery channel: the day it matters is exactly the day something is trying to make it
    /// unreachable.
    /// </remarks>
    Unavailable = 2,
}

/// <summary>A scanner's answer, and what to tell the operator when it is not Clean.</summary>
/// <param name="Signature">
/// What the scanner named, for an infected file. Recorded and returned, because "rejected" with no
/// reason is indistinguishable from a broken upload button.
/// </param>
/// <param name="Error">Why the scan did not happen, for <see cref="ScanVerdict.Unavailable"/>.</param>
public sealed record ScanResult(ScanVerdict Verdict, string? Signature = null, string? Error = null)
{
    public static readonly ScanResult Clean = new(ScanVerdict.Clean);

    public static ScanResult Infected(string signature) => new(ScanVerdict.Infected, signature);

    public static ScanResult Unavailable(string error) => new(ScanVerdict.Unavailable, Error: error);
}

/// <summary>Scans an upload before it is stored.</summary>
/// <remarks>
/// Before, not after. A file that is stored and then scanned is downloadable for the length of the
/// scan, and if the process dies in between it stays downloadable forever with nothing recording
/// that it was never checked.
/// </remarks>
public interface IFileScanner
{
    /// <summary>
    /// Whether a scanner is configured at all.
    /// </summary>
    /// <remarks>
    /// False is the default and means uploads are not scanned, which is what every deployment does
    /// today. The upload path reads this rather than inferring it from a Clean verdict, so "no
    /// scanner" and "scanner says fine" cannot be confused for one another in a log.
    /// </remarks>
    bool Configured { get; }

    /// <summary>Reads <paramref name="content"/> to the end and returns what it found.</summary>
    Task<ScanResult> ScanAsync(Stream content, CancellationToken ct = default);
}

/// <summary>The scanner used when none is configured. It scans nothing and says so.</summary>
/// <remarks>
/// Registered by default so the upload path has one object to talk to rather than a null check, and
/// so adding a scanner later is configuration rather than a code change. It returns Clean, which is
/// safe only because <see cref="Configured"/> is false and the upload path refuses to describe an
/// unscanned file as scanned.
/// </remarks>
internal sealed class NoFileScanner : IFileScanner
{
    public bool Configured => false;

    public Task<ScanResult> ScanAsync(Stream content, CancellationToken ct = default) =>
        Task.FromResult(ScanResult.Clean);
}
