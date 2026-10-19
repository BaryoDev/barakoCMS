using System.Text.Json;
using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using BarakoCMS.Import.Features.BulkCreate;
using FastEndpoints;
using Marten;
using Microsoft.Extensions.Configuration;
using Talaan;

namespace BarakoCMS.Import.Features.ImportSheet;

/// <summary>
/// POST /api/import/spreadsheet. Uploads an .xlsx/CSV with the header row, content type and column
/// mapping, and creates an entry from every data row, not only the 500 analyze previews.
/// </summary>
/// <remarks>
/// Authorised like the bulk create: the target content type's own create permission, checked for
/// the type and again for each row as stored. One request, one transaction. Bounded by the upload
/// size, the expanded size, <see cref="ImportLimits.MaxRecords"/> data rows and
/// <see cref="ImportLimits.MaxSeconds"/>; past any of them the answer is a refusal and nothing is
/// written. A crash or timeout midway rolls the transaction back, so there is no half import. A
/// second run creates the entries again, so use <c>dryRun</c> first; a uniqueness rule on the type
/// refuses the repeats.
/// </remarks>
internal sealed class Endpoint(
    IDocumentSession session,
    IPermissionResolver permissions,
    IContentBatchRunner runner,
    IConfiguration configuration) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Post("/api/import/spreadsheet");
        AllowFileUploads();
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var userIdClaim = User.FindFirst("UserId");
        if (userIdClaim == null || !Guid.TryParse(userIdClaim.Value, out var userId)
            || await session.LoadAsync<User>(userId, ct) is not { } user)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        if (string.IsNullOrWhiteSpace(req.ContentType))
        {
            await RefuseAsync("ContentType is required.", ct);
            return;
        }

        if (!await permissions.CanPerformActionAsync(user, req.ContentType, "create", null, ct))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        var lowered = req.ContentType.ToLower();
        var definition = await session.Query<ContentTypeDefinition>()
            .FirstOrDefaultAsync(d => d.Name.ToLower() == lowered, ct);
        if (definition is null)
        {
            await RefuseAsync($"There is no content type named {req.ContentType}.", ct);
            return;
        }

        var sheet = await ReadSheetAsync(ct);
        if (sheet is null)
            return;

        var plan = Plan(req, definition, sheet);
        if (plan is null)
        {
            await Send.ErrorsAsync(400, ct);
            return;
        }

        await RunAsync(req, user, definition, plan, ct);
    }

    private async Task RunAsync(
        Request req, User user, ContentTypeDefinition definition, SheetPlan plan, CancellationToken ct)
    {
        var status = Enum.TryParse<ContentStatus>(req.Status, true, out var parsed) ? parsed : ContentStatus.Published;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(ImportLimits.MaxSeconds(configuration)));

        BatchResult result;
        try
        {
            result = await RecordBatchWriter.WriteAsync(
                runner, permissions, user, definition, definition.Name, status, plan.Records, plan.RowNumbers,
                req.ContinueOnError, req.DryRun, HttpContext, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await Send.ResponseAsync(new Response(), 408, ct);
            return;
        }

        var response = new Response
        {
            Created = result.Created,
            Failed = result.Errors.Count,
            DataRows = plan.Records.Count,
            SkippedBlankRows = plan.SkippedBlank,
            DryRun = req.DryRun,
            Errors = result.Errors,
        };

        // A refused all-or-nothing batch is a 400 carrying the row errors, the same as the bulk create.
        var refused = result.Errors.Count > 0 && !req.ContinueOnError;
        await Send.ResponseAsync(response, refused ? 400 : 200, ct);
    }

    private async Task<SheetData?> ReadSheetAsync(CancellationToken ct)
    {
        var file = Files.Count > 0 ? Files[0] : null;
        if (file is null || file.Length == 0)
        {
            await RefuseAsync("An .xlsx or CSV file is required.", ct);
            return null;
        }

        var maxUpload = ImportLimits.MaxUploadBytes(configuration);
        if (file.Length > maxUpload)
        {
            await RefuseAsync(
                $"That file is {file.Length / 1024 / 1024} MB, over the {maxUpload / 1024 / 1024} MB "
              + $"this instance will import. Split it, or raise {ImportLimits.MaxUploadBytesKey}.", ct);
            return null;
        }

        using var buffer = new MemoryStream();
        await using (var upload = file.OpenReadStream())
            await upload.CopyToAsync(buffer, ct);
        buffer.Position = 0;

        var limit = SpreadsheetLimits.MaxExpandedBytes(configuration);
        if (SpreadsheetLimits.DeclaredExpandedBytes(buffer) is { } expanded && expanded > limit)
        {
            await RefuseAsync(
                $"That file expands to {expanded / 1024 / 1024} MB, over the {limit / 1024 / 1024} MB "
              + $"this instance will parse. Split it, or raise {SpreadsheetLimits.MaxExpandedBytesKey}.", ct);
            return null;
        }

        try
        {
            return Spreadsheet.Read(buffer, file.FileName);
        }
        catch (NotSupportedException ex)
        {
            await RefuseAsync(ex.Message, ct);
        }
        catch (Exception)
        {
            await RefuseAsync("Could not read the file. Ensure it is a valid .xlsx or CSV.", ct);
        }

        return null;
    }

    /// <summary>The mapped data rows, ready to write, or null after answering 400.</summary>
    private SheetPlan? Plan(Request req, ContentTypeDefinition definition, SheetData sheet)
    {
        var mapping = ReadMapping(req.Mapping, definition, sheet.ColumnCount, out var problem);
        if (mapping is null)
        {
            AddError(problem!);
            return null;
        }

        if (req.HeaderRow < 0 || req.HeaderRow >= sheet.RowCount)
        {
            AddError($"HeaderRow {req.HeaderRow} is outside the sheet, which has {sheet.RowCount} rows.");
            return null;
        }

        var maxRecords = ImportLimits.MaxRecords(configuration);
        var records = new List<Dictionary<string, object>>();
        var rowNumbers = new List<int>();
        var skipped = 0;

        for (var r = req.HeaderRow + 1; r < sheet.RowCount; r++)
        {
            var record = ReadRow(sheet, r, mapping);
            if (record.Count == 0)
            {
                skipped++;
                continue;
            }

            if (records.Count == maxRecords)
            {
                AddError(
                    $"One import may create at most {maxRecords} entries and this sheet holds more. "
                  + $"Nothing was written. Split the file, or raise {ImportLimits.MaxRecordsKey}.");
                return null;
            }

            records.Add(record);
            rowNumbers.Add(r);
        }

        if (records.Count == 0)
        {
            AddError("There are no data rows below the header.");
            return null;
        }

        return new SheetPlan(records, rowNumbers, skipped);
    }

    private static Dictionary<string, object> ReadRow(SheetData sheet, int row, Dictionary<string, int> mapping)
    {
        var record = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var (field, column) in mapping)
        {
            var cell = sheet.At(row, column);
            if (!cell.IsBlank)
                record[field] = cell.AsString();
        }

        return record;
    }

    private static Dictionary<string, int>? ReadMapping(
        string json, ContentTypeDefinition definition, int columns, out string? problem)
    {
        problem = null;
        Dictionary<string, int>? mapping;
        try
        {
            mapping = JsonSerializer.Deserialize<Dictionary<string, int>>(json);
        }
        catch (JsonException)
        {
            mapping = null;
        }

        if (mapping is null || mapping.Count == 0)
        {
            problem = "Mapping must be a JSON object of field name to column index, for example {\"Title\":0}.";
            return null;
        }

        foreach (var (field, column) in mapping)
        {
            if (!definition.Fields.Any(f => f is not null && string.Equals(f.Name, field, StringComparison.OrdinalIgnoreCase)))
                problem = $"{definition.Name} has no field named {field}.";
            else if (column < 0 || column >= columns)
                problem = $"Column {column} for {field} is outside the sheet, which has {columns} columns.";

            if (problem is not null)
                return null;
        }

        return mapping;
    }

    private sealed record SheetPlan(
        List<Dictionary<string, object>> Records, List<int> RowNumbers, int SkippedBlank);

    private async Task RefuseAsync(string message, CancellationToken ct)
    {
        AddError(message);
        await Send.ErrorsAsync(400, ct);
    }
}
