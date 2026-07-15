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
/// POST /api/import/analyze — accept an .xlsx/CSV upload and return a typed preview grid so a UI can
/// build a column mapping. Parses only; nothing is stored. Any authenticated user may analyze.
/// </summary>
public class Endpoint : EndpointWithoutRequest<Response>
{
    // Cap the preview so a huge upload can't balloon the response.
    private const int MaxPreviewRows = 500;

    public override void Configure()
    {
        Post("/api/import/analyze");
        AllowFileUploads();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var file = Files.Count > 0 ? Files[0] : null;
        if (file is null || file.Length == 0)
        {
            AddError("An .xlsx or CSV file is required.");
            await SendErrorsAsync(400, ct);
            return;
        }

        SheetData sheet;
        try
        {
            await using var stream = file.OpenReadStream();
            // Talaan buffers non-seekable streams internally for xlsx.
            sheet = Spreadsheet.Read(stream, file.FileName);
        }
        catch (NotSupportedException ex)
        {
            AddError(ex.Message);
            await SendErrorsAsync(400, ct);
            return;
        }
        catch (Exception)
        {
            AddError("Could not read the file. Ensure it is a valid .xlsx or CSV.");
            await SendErrorsAsync(400, ct);
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

        await SendAsync(new Response
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
