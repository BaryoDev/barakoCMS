using System.Globalization;
using System.Text;
using System.Text.Json;
using barakoCMS.Infrastructure.Auth;
using FastEndpoints;
using Marten;
using Microsoft.Extensions.Options;

namespace BarakoCMS.Forms.Features.Submissions.Export;

/// <summary>
/// GET /api/forms/{name}/submissions.csv. One header, one row per submission, newest first,
/// capped at <see cref="FormsOptions.ExportMaxRows"/>. Same gate and same date window as the list.
/// </summary>
public class Endpoint : Endpoint<SubmissionsRequest>
{
    private readonly IQuerySession _session;
    private readonly IOptions<FormsOptions> _options;

    public Endpoint(IQuerySession session, IOptions<FormsOptions> options)
    {
        _session = session;
        _options = options;
    }

    public override void Configure()
    {
        Get("/api/forms/{name}/submissions.csv");
        Definition.RequireCapability(FormsCapabilities.ViewFormSubmissions, FormsCapabilities.LegacyRoles);
    }

    public override async Task HandleAsync(SubmissionsRequest req, CancellationToken ct)
    {
        var form = await _session.Query<FormDefinition>().FirstOrDefaultAsync(f => f.Name == req.Name, ct);
        if (form is null) { await Send.NotFoundAsync(ct); return; }

        var max = _options.Value.ExportMaxRows > 0 ? _options.Value.ExportMaxRows : FormsOptions.DefaultExportMaxRows;
        var rows = await List.SubmissionQuery.For(_session, req.Name, req.From, req.To)
            .OrderByDescending(s => s.SubmittedAt)
            .Take(max)
            .ToListAsync(ct);

        var columns = form.Fields.Select(f => f.Name).ToList();
        var csv = Csv.Write(columns, rows);

        await Send.BytesAsync(
            Encoding.UTF8.GetBytes(csv), $"{form.Name}-submissions.csv", "text/csv; charset=utf-8", cancellation: ct);
    }
}

internal static class Csv
{
    public static string Write(IReadOnlyList<string> columns, IEnumerable<FormSubmission> rows)
    {
        var sb = new StringBuilder();
        sb.Append("id,submittedAt");
        foreach (var column in columns) sb.Append(',').Append(Cell(column));
        sb.Append("\r\n");

        foreach (var row in rows)
        {
            sb.Append(row.Id.ToString("D")).Append(',')
              .Append(row.SubmittedAt.ToString("O", CultureInfo.InvariantCulture));
            foreach (var column in columns)
            {
                sb.Append(',');
                if (row.Data.TryGetValue(column, out var value)) sb.Append(Cell(Text(value)));
            }
            sb.Append("\r\n");
        }

        return sb.ToString();
    }

    private static string Text(object? value) => value switch
    {
        null => string.Empty,
        string s => s,
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
        _ => JsonSerializer.Serialize(value),
    };

    /// <summary>
    /// Quotes what needs quoting, and defuses a value a spreadsheet would run as a formula. A
    /// visitor typed the cell, so "=HYPERLINK(...)" is exactly the kind of thing it can hold.
    /// </summary>
    private static string Cell(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            value = "'" + value;

        return value.IndexOfAny(['"', ',', '\r', '\n']) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }
}
