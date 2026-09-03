using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using BarakoCMS.Import;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BarakoCMS.Tests.Features.Import;

/// <summary>
/// <c>POST /api/import/analyze</c> refuses a file whose expanded size it will not parse.
/// </summary>
/// <remarks>
/// The parser reads a whole sheet into memory before the preview cap can apply, so the cost of the
/// request follows the expanded size rather than the uploaded size. An xlsx is a zip, so the request
/// body limit does not bound it: a file well inside the limit expands to many times its size.
///
/// Measured before the bound existed: a 3.2 MB upload took 98 seconds and 968 MB and returned a
/// 500 row preview. The tests here use a small file with a large declared expansion, so they assert
/// the refusal without spending the cost that made the refusal necessary.
/// </remarks>
[Collection("Sequential")]
public class AnalyzeLimitTests
{
    private readonly IntegrationTestFixture _factory;

    public AnalyzeLimitTests(IntegrationTestFixture factory) => _factory = factory;

    [Fact]
    public async Task A_file_that_expands_beyond_the_limit_is_refused_with_a_message_naming_the_setting()
    {
        var response = await AnalyzeAsync(Xlsx(rows: 200_000), "big.xlsx");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain(SpreadsheetLimits.MaxExpandedBytesKey,
            "an operator refused a file has to be told which setting decides it");
    }

    /// <summary>
    /// The pairing. Without it a bound that refused everything would satisfy the test above, and the
    /// feature would be gone rather than bounded.
    /// </summary>
    [Fact]
    public async Task An_ordinary_file_is_still_analyzed()
    {
        var response = await AnalyzeAsync(Xlsx(rows: 50), "small.xlsx");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain("\"rowCount\":50");
    }

    /// <summary>A CSV is not a zip, and the zip check must not refuse it.</summary>
    [Fact]
    public async Task A_csv_is_not_mistaken_for_an_archive()
    {
        var csv = Encoding.UTF8.GetBytes("Title,Body\nfirst,one\nsecond,two\n");

        var response = await AnalyzeAsync(csv, "rows.csv", "text/csv");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The measurement is on the declared expansion, not on the uploaded size, which is the whole
    /// point: the refused file below is smaller than the accepted one two tests up would be at the
    /// same row count, because it compresses better.
    /// </summary>
    [Fact]
    public void The_declared_expansion_is_read_without_decompressing()
    {
        using var stream = new MemoryStream(Xlsx(rows: 200_000));

        var declared = SpreadsheetLimits.DeclaredExpandedBytes(stream);

        declared.Should().NotBeNull("an xlsx is a zip and declares its uncompressed sizes");
        declared!.Value.Should().BeGreaterThan(SpreadsheetLimits.DefaultMaxExpandedBytes,
            "this is the file the refusal test relies on being over the limit");
        stream.Length.Should().BeLessThan(declared.Value,
            "the file on the wire is smaller than what it expands to, which is why the body limit "
          + "does not bound the parse");
        stream.Position.Should().Be(0, "the stream is handed on to the parser afterwards");
    }

    [Fact]
    public void A_non_archive_reports_no_declared_expansion()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("Title,Body\na,b\n"));

        SpreadsheetLimits.DeclaredExpandedBytes(stream).Should().BeNull();
    }

    [Fact]
    public void The_limit_is_configurable_and_falls_back_to_the_default()
    {
        SpreadsheetLimits.MaxExpandedBytes(new ConfigurationBuilder().Build())
            .Should().Be(SpreadsheetLimits.DefaultMaxExpandedBytes);

        var configured = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [SpreadsheetLimits.MaxExpandedBytesKey] = "99",
            })
            .Build();

        SpreadsheetLimits.MaxExpandedBytes(configured).Should().Be(99);
    }

    private async Task<HttpResponseMessage> AnalyzeAsync(
        byte[] bytes, string name,
        string contentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")
    {
        var client = _factory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(3);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: ["Admin"]));

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", name);

        return await client.PostAsync("/api/import/analyze", form, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A minimal xlsx with the given number of two-cell rows, built here rather than checked in so
    /// the repository does not carry a file whose only purpose is to be expensive.
    /// </summary>
    private static byte[] Xlsx(int rows)
    {
        var sheet = new StringBuilder();
        sheet.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sheet.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
        for (var r = 1; r <= rows; r++)
        {
            sheet.Append($"<row r=\"{r}\"><c r=\"A{r}\" t=\"inlineStr\"><is><t>x</t></is></c>");
            sheet.Append($"<c r=\"B{r}\" t=\"inlineStr\"><is><t>y</t></is></c></row>");
        }
        sheet.Append("</sheetData></worksheet>");

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, "[Content_Types].xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
              + "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">"
              + "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>"
              + "<Default Extension=\"xml\" ContentType=\"application/xml\"/>"
              + "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>"
              + "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>"
              + "</Types>");
            Write(zip, "_rels/.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
              + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
              + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>"
              + "</Relationships>");
            Write(zip, "xl/workbook.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
              + "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" "
              + "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">"
              + "<sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
            Write(zip, "xl/_rels/workbook.xml.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
              + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
              + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>"
              + "</Relationships>");
            Write(zip, "xl/worksheets/sheet1.xml", sheet.ToString());
        }

        return buffer.ToArray();
    }

    private static void Write(ZipArchive zip, string path, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(path, CompressionLevel.SmallestSize).Open());
        writer.Write(content);
    }
}
