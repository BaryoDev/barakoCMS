using System.IO.Compression;
using Microsoft.Extensions.Configuration;

namespace BarakoCMS.Import;

/// <summary>
/// How much spreadsheet this instance will parse.
/// </summary>
/// <remarks>
/// The parser reads a whole sheet into memory before anything can look at it, so the cost of a
/// request is set by the expanded size of the upload rather than by the size of the upload. An xlsx
/// is a zip, and a zip of repetitive XML compresses roughly fifteen to one, so the request body
/// limit does not bound the work: a file well inside it expands to many times its size, and the
/// in-memory grid is larger again.
///
/// Measured before this existed: a 3.2 MB xlsx, comfortably inside the 10 MB body limit, expanded to
/// 46 MB of sheet XML and took 98 seconds and 968 MB to answer, returning a preview of 500 rows. The
/// global rate limit is 100 requests a minute per address, which does not bound something that
/// costs that much.
///
/// The limit is on the expanded size, read from the zip's central directory without decompressing
/// anything, so a file is refused before it costs anything to open.
/// </remarks>
public static class SpreadsheetLimits
{
    /// <summary>Largest total uncompressed size an upload may declare. Default 8 MB.</summary>
    public const string MaxExpandedBytesKey = "Import:MaxExpandedBytes";

    public const long DefaultMaxExpandedBytes = 8L * 1024 * 1024;

    public static long MaxExpandedBytes(IConfiguration configuration) =>
        configuration.GetValue<long?>(MaxExpandedBytesKey) is { } configured && configured > 0
            ? configured
            : DefaultMaxExpandedBytes;

    /// <summary>
    /// The total uncompressed size the archive declares, or null when the bytes are not a zip.
    /// </summary>
    /// <remarks>
    /// Read from the central directory, which is what <c>ZipArchiveEntry.Length</c> exposes, so
    /// nothing is decompressed to answer this. A CSV is not a zip and needs no such check: its
    /// expanded size is its uploaded size, which the request body limit already bounds.
    ///
    /// A zip can of course lie about the figure. It cannot lie downwards and still be read, because
    /// the parser would then see less than it was promised, which is not a way to spend more.
    /// </remarks>
    public static long? DeclaredExpandedBytes(Stream seekable)
    {
        var start = seekable.Position;

        try
        {
            using var archive = new ZipArchive(seekable, ZipArchiveMode.Read, leaveOpen: true);

            long total = 0;
            foreach (var entry in archive.Entries)
            {
                total += entry.Length;
            }

            return total;
        }
        catch (InvalidDataException)
        {
            // Not a zip. A CSV reaches here, and so does a corrupt xlsx, which the parser refuses
            // with a message of its own.
            return null;
        }
        finally
        {
            seekable.Position = start;
        }
    }
}
