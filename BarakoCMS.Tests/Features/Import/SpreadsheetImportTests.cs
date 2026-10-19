using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Import;

/// <summary>
/// <c>POST /api/import/spreadsheet</c> imports every row of an uploaded sheet, where analyze only
/// previews 500, and says what it did with each bound it hits.
/// </summary>
[Collection("Sequential")]
public class SpreadsheetImportTests
{
    private readonly IntegrationTestFixture _fixture;

    public SpreadsheetImportTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private async Task<string> ContentTypeAsync()
    {
        var name = $"sheet{Guid.NewGuid():n}"[..12];
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = name,
            DisplayName = name,
            Fields =
            [
                new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string", IsRequired = true },
                new FieldDefinition { Name = "Body", DisplayName = "Body", Type = "string" },
            ],
        });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return name;
    }

    private async Task<HttpClient> ClientAsync(string role)
    {
        var userId = Guid.NewGuid();
        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new User
            {
                Id = userId,
                Username = $"sht-{Guid.NewGuid():n}"[..14],
                Email = $"sht-{Guid.NewGuid():n}@example.com",
                RoleIds = [role == "SuperAdmin" ? SystemRoles.SuperAdminRoleId : SystemRoles.UserRoleId],
            });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var client = _fixture.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(3);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _fixture.CreateToken([role], userId.ToString()));
        return client;
    }

    private async Task<int> CountAsync(string contentType)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return await session.Query<Content>()
            .CountAsync(c => c.ContentType == contentType, TestContext.Current.CancellationToken);
    }

    /// <summary>A CSV with a header row and <paramref name="rows"/> data rows. Row 5 has no title when asked.</summary>
    private static string Csv(int rows, int? blankTitleRow = null)
    {
        var csv = new StringBuilder("Name,Text\n");
        for (var i = 1; i <= rows; i++)
            csv.Append(i == blankTitleRow ? $",body {i}\n" : $"title {i},body {i}\n");
        return csv.ToString();
    }

    private static Task<HttpResponseMessage> PostAsync(
        HttpClient client, string type, string csv, bool continueOnError = false, bool dryRun = false,
        string? status = null, string mapping = "{\"Title\":0,\"Body\":1}")
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(type), "contentType" },
            { new StringContent("0"), "headerRow" },
            { new StringContent(mapping), "mapping" },
            { new StringContent(status ?? string.Empty), "status" },
            { new StringContent(continueOnError ? "true" : "false"), "continueOnError" },
            { new StringContent(dryRun ? "true" : "false"), "dryRun" },
        };
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(file, "file", "rows.csv");
        return client.PostAsync("/api/import/spreadsheet", form, TestContext.Current.CancellationToken);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;

    [Fact]
    public async Task A_sheet_longer_than_the_500_row_preview_is_imported_whole()
    {
        var type = await ContentTypeAsync();
        var client = await ClientAsync("SuperAdmin");

        var response = await PostAsync(client, type, Csv(700));

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var body = await JsonAsync(response);
        body.GetProperty("created").GetInt32().Should().Be(700);
        (await CountAsync(type)).Should().Be(700);
    }

    [Fact]
    public async Task A_sheet_over_the_row_limit_is_refused_naming_the_setting_and_writes_nothing()
    {
        var type = await ContentTypeAsync();
        var client = await ClientAsync("SuperAdmin");

        var response = await PostAsync(client, type, Csv(barakoCMS_Import_DefaultMax + 1));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain(BarakoCMS.Import.ImportLimits.MaxRecordsKey);
        (await CountAsync(type)).Should().Be(0);
    }

    private const int barakoCMS_Import_DefaultMax = BarakoCMS.Import.ImportLimits.DefaultMaxRecords;

    [Fact]
    public async Task One_bad_row_writes_nothing_and_is_named_by_its_sheet_row()
    {
        var type = await ContentTypeAsync();
        var client = await ClientAsync("SuperAdmin");

        var response = await PostAsync(client, type, Csv(10, blankTitleRow: 5));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var errors = (await JsonAsync(response)).GetProperty("errors");
        errors.GetArrayLength().Should().Be(1);
        errors[0].GetProperty("row").GetInt32().Should().Be(5, "the header is row 0, so data row 5 is sheet row 5");
        (await CountAsync(type)).Should().Be(0, "a crash or a bad row never leaves half a file behind");
    }

    [Fact]
    public async Task Continue_on_error_writes_the_good_rows_and_reports_the_bad_one()
    {
        var type = await ContentTypeAsync();
        var client = await ClientAsync("SuperAdmin");

        var response = await PostAsync(client, type, Csv(10, blankTitleRow: 5), continueOnError: true);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await JsonAsync(response);
        body.GetProperty("created").GetInt32().Should().Be(9);
        body.GetProperty("failed").GetInt32().Should().Be(1);
        (await CountAsync(type)).Should().Be(9);
    }

    [Fact]
    public async Task A_dry_run_reports_the_counts_and_writes_nothing()
    {
        var type = await ContentTypeAsync();
        var client = await ClientAsync("SuperAdmin");

        var response = await PostAsync(client, type, Csv(20), dryRun: true);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonAsync(response)).GetProperty("created").GetInt32().Should().Be(20);
        (await CountAsync(type)).Should().Be(0);
    }

    [Fact]
    public async Task A_caller_without_create_permission_on_the_type_is_refused_and_one_with_it_is_not()
    {
        var type = await ContentTypeAsync();

        var refused = await PostAsync(await ClientAsync("User"), type, Csv(3));
        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await CountAsync(type)).Should().Be(0);

        var allowed = await PostAsync(await ClientAsync("SuperAdmin"), type, Csv(3));
        allowed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await CountAsync(type)).Should().Be(3);
    }

    [Fact]
    public async Task A_status_that_is_not_a_name_is_refused_and_writes_nothing()
    {
        var type = await ContentTypeAsync();
        var client = await ClientAsync("SuperAdmin");

        (await PostAsync(client, type, Csv(3), status: "Draf")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await PostAsync(client, type, Csv(3), status: "1")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await CountAsync(type)).Should().Be(0);

        (await PostAsync(client, type, Csv(3), status: "draft")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await CountAsync(type)).Should().Be(3);
    }

    [Fact]
    public async Task Mapping_keys_that_differ_only_by_case_are_refused()
    {
        var type = await ContentTypeAsync();
        var client = await ClientAsync("SuperAdmin");

        var response = await PostAsync(client, type, Csv(3), mapping: "{\"Title\":0,\"title\":1}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await CountAsync(type)).Should().Be(0);
    }

    [Fact]
    public async Task A_cell_in_a_far_column_or_row_is_refused_before_the_sheet_is_read_by_both_endpoints()
    {
        var type = await ContentTypeAsync();
        var client = await ClientAsync("SuperAdmin");

        var ordinary = Xlsx(["A1", "B1", "A2", "B2"]);
        var farColumn = Xlsx(["A1", "B1", "A2", "ZZZZZZ2"]);
        var farRow = Xlsx(["A1", "B1", "A2", "A4000000000"]);

        (await UploadAsync(client, "/api/import/analyze", ordinary)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await UploadAsync(client, "/api/import/spreadsheet", ordinary, type)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await CountAsync(type)).Should().Be(1);

        foreach (var bad in new[] { farColumn, farRow })
        {
            var analyze = await UploadAsync(client, "/api/import/analyze", bad);
            analyze.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await analyze.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
                .Should().ContainAny("Import:MaxColumns", "Import:MaxRecords");

            var import = await UploadAsync(client, "/api/import/spreadsheet", bad, type);
            import.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        (await CountAsync(type)).Should().Be(1, "the refused uploads wrote nothing");
    }

    private static Task<HttpResponseMessage> UploadAsync(HttpClient client, string url, byte[] bytes, string? type = null)
    {
        var form = new MultipartFormDataContent();
        if (type is not null)
        {
            form.Add(new StringContent(type), "contentType");
            form.Add(new StringContent("0"), "headerRow");
            form.Add(new StringContent("{\"Title\":0,\"Body\":1}"), "mapping");
        }
        form.Add(new ByteArrayContent(bytes), "file", "sheet.xlsx");
        return client.PostAsync(url, form, TestContext.Current.CancellationToken);
    }

    /// <summary>A tiny xlsx holding one inline-string cell at each given address.</summary>
    private static byte[] Xlsx(string[] addresses)
    {
        var rows = addresses
            .GroupBy(a => new string(a.SkipWhile(char.IsLetter).ToArray()))
            .Select(g => $"<row r=\"{g.Key}\">" + string.Concat(g.Select(a =>
                $"<c r=\"{a}\" t=\"inlineStr\"><is><t>v</t></is></c>")) + "</row>");
        var sheet = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
            + "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>"
            + string.Concat(rows) + "</sheetData></worksheet>";

        using var buffer = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(buffer, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            const string xml = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>";
            Add(zip, "[Content_Types].xml", xml + "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">"
                + "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>"
                + "<Default Extension=\"xml\" ContentType=\"application/xml\"/>"
                + "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>"
                + "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/></Types>");
            Add(zip, "_rels/.rels", xml + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
            Add(zip, "xl/workbook.xml", xml + "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" "
                + "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">"
                + "<sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
            Add(zip, "xl/_rels/workbook.xml.rels", xml + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>");
            Add(zip, "xl/worksheets/sheet1.xml", sheet);
        }

        return buffer.ToArray();
    }

    private static void Add(System.IO.Compression.ZipArchive zip, string path, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(path).Open());
        writer.Write(content);
    }

    [Fact]
    public async Task A_mapping_naming_a_field_the_type_lacks_is_refused()
    {
        var type = await ContentTypeAsync();
        var client = await ClientAsync("SuperAdmin");
        var form = new MultipartFormDataContent
        {
            { new StringContent(type), "contentType" },
            { new StringContent("{\"Nope\":0}"), "mapping" },
        };
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(Csv(3)));
        form.Add(file, "file", "rows.csv");

        var response = await client.PostAsync("/api/import/spreadsheet", form, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await CountAsync(type)).Should().Be(0);
    }
}
