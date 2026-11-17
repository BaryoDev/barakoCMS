using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Talaan;

namespace BarakoCMS.Import;

/// <summary>Reading an uploaded spreadsheet: the part the analyze and the import routes share.</summary>
internal static class SpreadsheetUpload
{
    /// <summary>
    /// Buffers the upload, refuses one that expands past the limit or that the row and column guard
    /// refuses, then parses it. Every refusal goes through <paramref name="refuse"/> and returns null.
    /// </summary>
    /// <param name="guard">The route's own row and column check over the buffered bytes, null when it passes.</param>
    /// <param name="refuse">Answers the caller; each route reports errors its own way.</param>
    public static async Task<SheetData?> ReadAsync(
        IFormFile file, IConfiguration configuration, Func<Stream, string?> guard,
        Func<string, Task> refuse, CancellationToken ct)
    {
        // Buffered once so the archive can be measured and then parsed from the same bytes. Bounded
        // by the request body limit, so this is the size already accepted rather than a new cost.
        using var buffer = new MemoryStream();
        await using (var upload = file.OpenReadStream())
            await upload.CopyToAsync(buffer, ct);
        buffer.Position = 0;

        // Refused before anything is decompressed. The parser materialises the whole sheet, so the
        // cost is set by the expanded size rather than the uploaded size, and an xlsx is a zip: a
        // file well inside the body limit expands to many times its size. See SpreadsheetLimits.
        var limit = SpreadsheetLimits.MaxExpandedBytes(configuration);
        if (SpreadsheetLimits.ExpandedBytesOver(buffer, limit) is { } expanded)
        {
            await refuse(
                $"That file expands to {expanded / 1024 / 1024} MB, over the {limit / 1024 / 1024} MB "
              + $"this instance will parse. Split it, or raise {SpreadsheetLimits.MaxExpandedBytesKey}.");
            return null;
        }

        if (guard(buffer) is { } refusal)
        {
            await refuse(refusal);
            return null;
        }

        try
        {
            return Spreadsheet.Read(buffer, file.FileName);
        }
        catch (NotSupportedException ex)
        {
            await refuse(ex.Message);
        }
        catch (Exception)
        {
            await refuse("Could not read the file. Ensure it is a valid .xlsx or CSV.");
        }

        return null;
    }
}
