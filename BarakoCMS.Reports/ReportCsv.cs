using System.Globalization;
using System.Text;

namespace BarakoCMS.Reports;

/// <summary>
/// Writes a run as CSV. A cell a spreadsheet would read as a formula is defused, because a group
/// key is whatever a visitor typed into a field.
/// </summary>
internal static class ReportCsv
{
    /// <summary>
    /// The cell as text. A string that starts with =, +, - or @, or with a tab or carriage return
    /// (which some programs trim before reading), gets a leading apostrophe, then is quoted when it
    /// holds a comma, a quote or a line break. Numbers are written bare: they cannot be formulas.
    /// </summary>
    public static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }

    public static string Write(string groupColumn, IReadOnlyList<string> measureLabels, ReportResult result)
    {
        var columns = measureLabels.Contains("count", StringComparer.Ordinal)
            ? measureLabels
            : ["count", .. measureLabels];

        var csv = new StringBuilder();
        csv.Append(Cell(groupColumn));
        foreach (var column in columns) csv.Append(',').Append(Cell(column));
        csv.Append("\r\n");

        foreach (var row in result.Rows) Line(csv, row.Key, row, columns);
        Line(csv, "Total", result.Totals, columns);
        return csv.ToString();
    }

    private static void Line(StringBuilder csv, string? key, ReportRow row, IReadOnlyList<string> columns)
    {
        csv.Append(Cell(key));
        foreach (var column in columns)
        {
            var figure = column == "count" && !row.Measures.ContainsKey("count")
                ? row.Count
                : row.Measures.GetValueOrDefault(column);
            csv.Append(',').Append(figure?.ToString(CultureInfo.InvariantCulture));
        }

        csv.Append("\r\n");
    }
}
