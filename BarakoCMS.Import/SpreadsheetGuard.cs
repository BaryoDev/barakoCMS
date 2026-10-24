using System.IO.Compression;
using System.Xml;

namespace BarakoCMS.Import;

/// <summary>
/// Refuses a workbook whose cell addresses would make the parser allocate far more than the file
/// is large.
/// </summary>
/// <remarks>
/// The reader pads every row with empty cells up to the highest column it sees, and pads the sheet
/// with empty rows up to the highest row index, so one cell at <c>ZZZZZZ1</c>, or one at row
/// 4,000,000,000, inside an 8 MB expanded sheet allocates gigabytes. The expanded-size limit cannot
/// see that. This reads the worksheet XML as a stream, never a DOM, and looks only at the addresses.
/// </remarks>
internal static class SpreadsheetGuard
{
    /// <summary>
    /// Excel's own row limit, the row cap for analyze. Analyze accepted any row count before, so its
    /// cap sits at what a real sheet can hold. Padding rows share one empty array, so a million
    /// references is a few MB.
    /// </summary>
    public const long ExcelMaxRows = 1_048_576;

    /// <summary>
    /// Cells the parser may hold once the sheet is padded to a rectangle. Capping rows and columns
    /// alone still allows their product; an 8 MB sheet of real cells stays far under this.
    /// </summary>
    public const long MaxPaddedCells = 2_000_000;

    /// <summary>Blank rows tolerated past the row limit, so a trailing formatted range does not refuse a sheet.</summary>
    public const int RowMargin = 100;

    /// <summary>The message to refuse with, or null when the file is fine or is not a workbook.</summary>
    public static string? Check(Stream seekable, int maxColumns, long maxRowIndex)
    {
        var start = seekable.Position;

        ZipArchive archive;
        try
        {
            archive = new ZipArchive(seekable, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException)
        {
            seekable.Position = start;
            return null;
        }

        try
        {
            using (archive)
            {
                foreach (var entry in archive.Entries)
                {
                    if (!entry.FullName.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase)
                        || !entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                        continue;

                    using var stream = entry.Open();
                    if (CheckSheet(stream, maxColumns, maxRowIndex) is { } refusal)
                        return refusal;
                }

                return null;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or XmlException)
        {
            return "Could not read the file. Ensure it is a valid .xlsx or CSV.";
        }
        finally
        {
            seekable.Position = start;
        }
    }

    private static string? CheckSheet(Stream xml, int maxColumns, long maxRowIndex)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var reader = XmlReader.Create(xml, settings);
        // Indexed as the parser indexes: every row or cell element takes the next position, or its
        // address when that is further on, so a missing, repeated or backwards address still counts.
        long row = 0, column = 0, highestRow = 0, widest = 0;

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element)
                continue;

            var address = reader.GetAttribute("r");
            if (reader.LocalName == "row")
            {
                if (address is not null && !long.TryParse(address, out _))
                    return Unreadable(address);
                row = Math.Max(row + 1, address is null ? 0 : long.Parse(address));
                column = 0;
            }
            else if (reader.LocalName == "c")
            {
                var cell = address is null ? null : ReadAddress(address);
                if (address is not null && cell is null)
                    return Unreadable(address);
                column = Math.Max(column + 1, cell?.Column ?? 0);
                row = Math.Max(row, cell?.Row ?? 0);
                if (column > maxColumns)
                    return $"A cell sits in column {column}, past the {maxColumns} columns this instance will read. "
                         + $"Nothing was read. Trim the sheet, or raise {SpreadsheetLimits.MaxColumnsKey}.";
                widest = Math.Max(widest, column);
            }
            else
                continue;

            if (row > maxRowIndex)
                return RowRefusal(row, maxRowIndex);
            highestRow = Math.Max(highestRow, row);

            // The parser pads every row to the widest column in the sheet, so the cost is the rectangle.
            if (highestRow * widest > MaxPaddedCells)
                return $"The sheet spreads its cells over more than {MaxPaddedCells:N0} positions. Nothing was read. "
                     + "Remove the empty columns and rows between the data.";
        }

        return null;
    }

    private static string Unreadable(string address) =>
        $"The sheet holds an address this instance cannot read: {address[..Math.Min(address.Length, 20)]}. Nothing was read.";

    private static string RowRefusal(long row, long maxRowIndex) =>
        $"A cell sits in row {row}, past the {maxRowIndex} rows this instance will read. Nothing was read. "
      + $"Trim the sheet, or raise {ImportLimits.MaxRecordsKey}.";

    /// <summary>One-based column and row of an A1 style address. Letters beyond eight count as far past any limit.</summary>
    private static (long Column, long Row)? ReadAddress(string address)
    {
        var i = 0;
        long column = 0;
        for (; i < address.Length && char.IsAsciiLetter(address[i]); i++)
            column = Math.Min(column * 26 + (char.ToUpperInvariant(address[i]) - 'A' + 1), int.MaxValue);

        if (i == 0 || !long.TryParse(address.AsSpan(i), out var row))
            return null;

        return (column, row);
    }
}
