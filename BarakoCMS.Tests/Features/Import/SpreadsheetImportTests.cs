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
        HttpClient client, string type, string csv, bool continueOnError = false, bool dryRun = false)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(type), "contentType" },
            { new StringContent("0"), "headerRow" },
            { new StringContent("{\"Title\":0,\"Body\":1}"), "mapping" },
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
