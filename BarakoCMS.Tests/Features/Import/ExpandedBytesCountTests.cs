using System.IO.Compression;
using BarakoCMS.Import;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests.Features.Import;

public class ExpandedBytesCountTests
{
    private const long Limit = 1024 * 1024;

    [Fact]
    public void An_entry_that_declares_a_small_size_but_holds_more_is_counted_by_what_it_holds()
    {
        var zip = ZipWithEntry(new string('a', 4 * 1024 * 1024));
        SetDeclaredSize(zip, 10);

        using var stream = new MemoryStream(zip);

        SpreadsheetLimits.DeclaredExpandedBytes(stream)
            .Should().Be(10, "the directory was rewritten to claim ten bytes");
        SpreadsheetLimits.ExpandedBytesOver(stream, Limit)
            .Should().NotBeNull("the real content is over the limit whatever the directory says")
            .And.Subject.Value.Should().BeGreaterThan(Limit);
        stream.Position.Should().Be(0);
    }

    [Fact]
    public void An_honest_small_archive_is_not_over_the_limit()
    {
        using var stream = new MemoryStream(ZipWithEntry("hello"));

        SpreadsheetLimits.ExpandedBytesOver(stream, Limit).Should().BeNull();
        SpreadsheetLimits.ActualExpandedBytes(stream, Limit).Should().Be(5);
    }

    [Fact]
    public void A_non_archive_has_no_counted_size()
    {
        using var stream = new MemoryStream("a,b\n1,2\n"u8.ToArray());

        SpreadsheetLimits.ActualExpandedBytes(stream, Limit).Should().BeNull();
        SpreadsheetLimits.ExpandedBytesOver(stream, Limit).Should().BeNull();
    }

    [Fact]
    public void A_corrupt_first_entry_does_not_hide_a_large_later_one()
    {
        var zip = ZipWithEntries(
            ("a.xml", new string('a', 1000)),
            ("b.xml", new string('b', 4 * 1024 * 1024)));
        zip[30 + "a.xml".Length] = 0xFF;

        using var stream = new MemoryStream(zip);

        SpreadsheetLimits.ExpandedBytesOver(stream, Limit)
            .Should().NotBeNull("an archive with an unreadable entry cannot be measured, so it is refused");
    }

    [Fact]
    public void A_worksheet_entry_that_cannot_be_read_is_refused_not_passed()
    {
        var zip = ZipWithEntries(("xl/worksheets/sheet1.xml", new string('a', 1000)));
        zip[30 + "xl/worksheets/sheet1.xml".Length] = 0xFF;

        using var stream = new MemoryStream(zip);

        SpreadsheetGuard.Check(stream, 256, 1_000_000).Should().NotBeNull();
    }

    private static byte[] ZipWithEntry(string content) => ZipWithEntries(("xl/worksheets/sheet1.xml", content));

    private static byte[] ZipWithEntries(params (string Name, string Content)[] entries)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(content);
            }
        }

        return output.ToArray();
    }

    /// <summary>Rewrites the uncompressed size in the central directory record, offset 24 from its signature.</summary>
    private static void SetDeclaredSize(byte[] zip, uint size)
    {
        for (var i = zip.Length - 4; i >= 0; i--)
        {
            if (zip[i] == 0x50 && zip[i + 1] == 0x4B && zip[i + 2] == 0x01 && zip[i + 3] == 0x02)
            {
                BitConverter.GetBytes(size).CopyTo(zip, i + 24);
                return;
            }
        }

        throw new InvalidOperationException("no central directory record");
    }
}
