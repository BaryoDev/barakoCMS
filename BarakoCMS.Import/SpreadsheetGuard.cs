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
    /// <summary>Blank rows tolerated past the row limit, so a trailing formatted range does not refuse a sheet.</summary>
    public const int RowMargin = 100;

    /// <summary>The message to refuse with, or null when the file is fine or is not a workbook.</summary>
    public static string? Check(Stream seekable, int maxColumns, long maxRowIndex)
    {
        var start = seekable.Position;
        try
        {
            using var archive = new ZipArchive(seekable, ZipArchiveMode.Read, leaveOpen: true);
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
        catch (InvalidDataException)
        {
            return null;
        }
        catch (XmlException)
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

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.Depth > 4)
                continue;

            var address = reader.GetAttribute("r");
            if (address is null)
                continue;

            if (reader.LocalName == "row" && long.TryParse(address, out var row) && row > maxRowIndex)
                return RowRefusal(row, maxRowIndex);

            if (reader.LocalName == "c" && ReadAddress(address) is { } cell)
            {
                var (column, cellRow) = cell;
                if (column > maxColumns)
                    return $"A cell sits in column {column}, past the {maxColumns} columns this instance will read. "
                         + $"Nothing was read. Trim the sheet, or raise {SpreadsheetLimits.MaxColumnsKey}.";
                if (cellRow > maxRowIndex)
                    return RowRefusal(cellRow, maxRowIndex);
            }
        }

        return null;
    }

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
