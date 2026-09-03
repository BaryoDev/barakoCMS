using barakoCMS.Infrastructure.Auth;
using FastEndpoints;
using Talaan;

namespace BarakoCMS.Import.Features.Analyze;

public class Response
{
    public int RowCount { get; set; }
    public int ColumnCount { get; set; }
    /// <summary>Best-effort header row (first row with two or more non-blank cells), or -1.</summary>
    public int SuggestedHeaderRow { get; set; }
    public bool Truncated { get; set; }
    public List<List<CellDto>> Rows { get; set; } = new();

    public class CellDto
    {
        public string Kind { get; set; } = "Empty";
        public string Value { get; set; } = string.Empty;
    }
}

/// <summary>
/// POST /api/import/analyze, which accepts an .xlsx/CSV upload and return a typed preview grid so a UI can
/// build a column mapping. Parses only; nothing is stored. Any authenticated user may analyze.
/// </summary>
public class Endpoint : EndpointWithoutRequest<Response>
{
    // Cap the preview so a huge upload can't balloon the response. This bounds what comes back and
    // nothing about what it costs to produce: see SpreadsheetLimits for the half that does.
    private const int MaxPreviewRows = 500;

    private readonly Microsoft.Extensions.Configuration.IConfiguration _configuration;

    public Endpoint(Microsoft.Extensions.Configuration.IConfiguration configuration) =>
        _configuration = configuration;

    public override void Configure()
    {
        Post("/api/import/analyze");
        AllowFileUploads();
        // It had no gate at all before this: any authenticated caller could hand the server a
        // spreadsheet to parse, and parsing is the expensive part. The bulk create next door asks
        // the target content type's own create permission, which is the right question for a write
        // and one this endpoint cannot ask, because the mapping that names the target is built from
        // the preview it is about to return.
        Definition.RequireCapability(
            ImportCapabilities.AnalyzeSpreadsheets, ImportCapabilities.LegacyRoles);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var file = Files.Count > 0 ? Files[0] : null;
        if (file is null || file.Length == 0)
        {
            AddError("An .xlsx or CSV file is required.");
            await Send.ErrorsAsync(400, ct);
            return;
        }

        // Buffered once so the archive can be measured and then parsed from the same bytes. Bounded
        // by the request body limit, so this is the size already accepted rather than a new cost.
        using var buffer = new MemoryStream();
        await using (var upload = file.OpenReadStream())
        {
            await upload.CopyToAsync(buffer, ct);
        }
        buffer.Position = 0;

        // Refused before anything is decompressed. The parser materialises the whole sheet, so the
        // cost of this request is set by the expanded size rather than the uploaded size, and an
        // xlsx is a zip: a file well inside the body limit expands to many times its size. See
        // SpreadsheetLimits for the measurement that produced the default.
        var limit = SpreadsheetLimits.MaxExpandedBytes(_configuration);
        if (SpreadsheetLimits.DeclaredExpandedBytes(buffer) is { } expanded && expanded > limit)
        {
            AddError(
                $"That file expands to {expanded / 1024 / 1024} MB, over the {limit / 1024 / 1024} MB "
              + $"this instance will parse. Split it, or raise {SpreadsheetLimits.MaxExpandedBytesKey}.");
            await Send.ErrorsAsync(400, ct);
            return;
        }

        SheetData sheet;
        try
        {
            sheet = Spreadsheet.Read(buffer, file.FileName);
        }
        catch (NotSupportedException ex)
        {
            AddError(ex.Message);
            await Send.ErrorsAsync(400, ct);
            return;
        }
        catch (Exception)
        {
            AddError("Could not read the file. Ensure it is a valid .xlsx or CSV.");
            await Send.ErrorsAsync(400, ct);
            return;
        }

        var take = Math.Min(sheet.RowCount, MaxPreviewRows);
        var rows = new List<List<Response.CellDto>>(take);
        for (var r = 0; r < take; r++)
        {
            var rowCells = new List<Response.CellDto>(sheet.ColumnCount);
            for (var c = 0; c < sheet.ColumnCount; c++)
            {
                var cell = sheet.At(r, c);
                rowCells.Add(new Response.CellDto { Kind = cell.Kind.ToString(), Value = cell.AsString() });
            }
            rows.Add(rowCells);
        }

        await Send.ResponseAsync(new Response
        {
            RowCount = sheet.RowCount,
            ColumnCount = sheet.ColumnCount,
            SuggestedHeaderRow = SuggestHeaderRow(sheet),
            Truncated = sheet.RowCount > take,
            Rows = rows
        }, cancellation: ct);
    }

    private static int SuggestHeaderRow(SheetData sheet)
    {
        for (var r = 0; r < sheet.RowCount; r++)
        {
            var nonBlank = 0;
            for (var c = 0; c < sheet.ColumnCount; c++)
                if (!sheet.At(r, c).IsBlank) nonBlank++;
            if (nonBlank >= 2) return r;
        }
        return -1;
    }
}
