namespace BarakoCMS.Files;

/// <summary>How wide a caller may ask for, and which widths actually get made.</summary>
public sealed class ImageVariantOptions
{
    public const string Section = "Files:Images";

    /// <summary>
    /// The widest variant anyone may request, in pixels. Zero or less turns variants off entirely.
    /// </summary>
    /// <remarks>
    /// The public download is anonymous, so an uncapped width is a way for anyone to spend the
    /// server's CPU and disk for free. 2048 covers a full-bleed hero on a retina display and is
    /// small enough that a decode plus a resample is milliseconds rather than seconds.
    /// </remarks>
    public int MaxWidth { get; set; } = 2048;

    /// <summary>
    /// The widest source image that will be resized at all, in pixels of area.
    /// </summary>
    /// <remarks>
    /// A ten megabyte PNG is allowed by the upload cap and can still decode to a bitmap of tens of
    /// gigabytes, because the limit on the wire is compressed bytes and the limit that matters is
    /// pixels. Dimensions are read from the header before any pixel is decoded, and anything over
    /// this is served unresized rather than refused: the original is what the caller would have got
    /// yesterday, so declining to resize costs nobody anything.
    /// </remarks>
    public long MaxSourcePixels { get; set; } = 50_000_000;

    /// <summary>Whether a <c>?w=</c> is honoured at all.</summary>
    public bool Enabled => MaxWidth > 0;

    /// <summary>
    /// The widths a variant is ever actually made at, before the cap is applied.
    /// </summary>
    /// <remarks>
    /// Requests are snapped onto this ladder rather than honoured literally, and that is the whole
    /// defence against the cache being the attack. Honouring an arbitrary width means an anonymous
    /// caller can walk <c>?w=1</c> through <c>?w=2048</c> and leave two thousand stored blobs behind
    /// per public file, which is a cap on CPU per request and no cap at all on what the cap costs.
    /// Snapping bounds it at one variant per rung.
    /// </remarks>
    private static readonly int[] Ladder = [160, 320, 640, 960, 1280, 1920, 2560];

    /// <summary>
    /// The rungs this deployment will make, which is the ladder trimmed to the cap with the cap
    /// itself on the end so that every allowed request has a rung at or above it.
    /// </summary>
    public IReadOnlyList<int> Widths =>
        Enabled ? [.. Ladder.Where(w => w < MaxWidth), MaxWidth] : [];

    /// <summary>The rung a request is served from, or null if the request is out of range.</summary>
    public int? Snap(int requested)
    {
        if (!Enabled || requested < 1 || requested > MaxWidth)
        {
            return null;
        }

        foreach (var rung in Widths)
        {
            if (rung >= requested)
            {
                return rung;
            }
        }

        return MaxWidth;
    }
}

/// <summary>Makes a narrower copy of an image, or declines.</summary>
public interface IImageResizer
{
    /// <summary>
    /// Whether this content type is one the resizer will decode.
    /// </summary>
    /// <remarks>
    /// Asked before any bytes are loaded, so a PDF with a <c>?w=</c> on it is answered with the PDF
    /// rather than with a 500 from a decoder that was never going to succeed.
    /// </remarks>
    bool CanResize(string contentType);

    /// <summary>
    /// Returns the resized bytes, or null when the sensible answer is the original.
    /// </summary>
    /// <remarks>
    /// Null rather than an exception for every "no": already narrower than asked, larger than
    /// <see cref="ImageVariantOptions.MaxSourcePixels"/>, or bytes that do not decode. The caller
    /// serves the original in all three cases, which is what it would have served anyway.
    /// </remarks>
    Task<byte[]?> ResizeAsync(byte[] source, int width, CancellationToken ct = default);
}
