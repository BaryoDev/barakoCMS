using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Import;

/// <summary>
/// Bulk import, and what it does with a row it cannot accept.
/// </summary>
/// <remarks>
/// A spreadsheet import is a single click that writes hundreds of records, so the question worth
/// pinning is what happens on row 137 of 400. The endpoint answers it two ways deliberately: by
/// default nothing is written unless every row is good, and with <c>continueOnError</c> the good
/// rows land and the bad ones come back named. Both are defensible. Silently applying half a file
/// and reporting success is not, and it is what an import that validated as it wrote would do.
/// </remarks>
[Collection("Sequential")]
public class BulkCreateTests
{
    private readonly IntegrationTestFixture _fixture;

    public BulkCreateTests(IntegrationTestFixture fixture) => _fixture = fixture;

    /// <summary>A content type with one required field, so a row can be invalid on purpose.</summary>
    private async Task<string> ContentTypeAsync()
    {
        var name = $"import{Guid.NewGuid():n}"[..12];
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
            var roleId = role == "SuperAdmin" ? SystemRoles.SuperAdminRoleId : SystemRoles.UserRoleId;
            session.Store(new User
            {
                Id = userId,
                Username = $"imp-{Guid.NewGuid():n}"[..14],
                Email = $"imp-{Guid.NewGuid():n}@example.com",
                RoleIds = [roleId],
            });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var client = _fixture.CreateClient();
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

    /// <summary>
    /// The positive control, and the module's main job: the rows asked for become documents.
    /// </summary>
    [Fact]
    public async Task A_bulk_create_produces_the_documents_it_reports()
    {
        var type = await ContentTypeAsync();
        var client = await ClientAsync("SuperAdmin");

        var response = await client.PostAsJsonAsync("/api/import/content", new
        {
            contentType = type,
            records = new[]
            {
                new Dictionary<string, object> { ["Title"] = "First" },
                new Dictionary<string, object> { ["Title"] = "Second" },
            },
        }, TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", response.StatusCode,
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        using var report = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        report.RootElement.GetProperty("created").GetInt32().Should().Be(2);

        (await CountAsync(type)).Should().Be(2,
            "the count in the report is only worth anything if the documents are actually there");
    }

    /// <summary>
    /// One bad row in an all-or-nothing import writes nothing at all, including the good rows
    /// that came before it.
    /// </summary>
    [Fact]
    public async Task An_invalid_row_stops_the_whole_import_and_nothing_is_written()
    {
        var type = await ContentTypeAsync();
        var client = await ClientAsync("SuperAdmin");

        var response = await client.PostAsJsonAsync("/api/import/content", new
        {
            contentType = type,
            records = new[]
            {
                new Dictionary<string, object> { ["Title"] = "Good row, first" },
                new Dictionary<string, object> { ["Title"] = "" },
                new Dictionary<string, object> { ["Title"] = "Good row, third" },
            },
        }, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var report = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        report.RootElement.GetProperty("created").GetInt32().Should().Be(0);
        report.RootElement.GetProperty("errors").EnumerateArray()
            .Select(e => e.GetProperty("row").GetInt32())
            .Should().BeEquivalentTo([1], "the report names the row, so the file can be fixed");

        (await CountAsync(type)).Should().Be(0,
            "a partly applied import leaves a state nobody designed and no clear way back");
    }

    /// <summary>
    /// And with the flag set, the good rows land and the bad ones come back named rather than lost.
    /// </summary>
    [Fact]
    public async Task With_continue_on_error_the_good_rows_land_and_the_bad_ones_are_named()
    {
        var type = await ContentTypeAsync();
        var client = await ClientAsync("SuperAdmin");

        var response = await client.PostAsJsonAsync("/api/import/content", new
        {
            contentType = type,
            continueOnError = true,
            records = new[]
            {
                new Dictionary<string, object> { ["Title"] = "Keeps" },
                new Dictionary<string, object> { ["Title"] = "" },
            },
        }, TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.Should().BeTrue();

        using var report = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        report.RootElement.GetProperty("created").GetInt32().Should().Be(1);
        report.RootElement.GetProperty("failed").GetInt32().Should().Be(1);

        (await CountAsync(type)).Should().Be(1);
    }

    /// <summary>
    /// An empty request is refused rather than treated as an import of nothing that succeeded.
    /// </summary>
    [Fact]
    public async Task A_request_with_no_records_is_refused()
    {
        var client = await ClientAsync("SuperAdmin");

        (await client.PostAsJsonAsync("/api/import/content",
                new { contentType = "anything", records = Array.Empty<object>() },
                TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// Authorisation is the target type's own create permission, not a fixed role, so a caller
    /// without it is refused even though the endpoint names no roles.
    /// </summary>
    [Fact]
    public async Task A_caller_without_create_permission_on_the_type_is_refused()
    {
        var type = await ContentTypeAsync();
        var client = await ClientAsync("User");

        var response = await client.PostAsJsonAsync("/api/import/content", new
        {
            contentType = type,
            records = new[] { new Dictionary<string, object> { ["Title"] = "Not mine to write" } },
        }, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "bulk create is still create, and writing four hundred rows is not a smaller permission "
            + "than writing one");

        (await CountAsync(type)).Should().Be(0);
    }

    /// <summary>
    /// Analyze parses a CSV into a preview grid, and stores nothing.
    /// </summary>
    [Fact]
    public async Task Analyze_returns_a_preview_grid_for_a_csv()
    {
        var client = await ClientAsync("SuperAdmin");

        var response = await client.PostAsync(
            "/api/import/analyze", Upload("Title,Body\nFirst,One\nSecond,Two\n", "rows.csv"),
            TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", response.StatusCode,
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        using var preview = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        preview.RootElement.GetProperty("rowCount").GetInt32().Should().Be(3);
        preview.RootElement.GetProperty("columnCount").GetInt32().Should().Be(2);
        preview.RootElement.GetProperty("suggestedHeaderRow").GetInt32().Should().Be(0);
    }

    /// <summary>
    /// A file that is not a spreadsheet is refused with an error, not accepted as an empty one.
    /// </summary>
    /// <remarks>
    /// An empty preview looks the same as a file that genuinely had no rows, so the person uploading
    /// would map columns that are not there and wonder why the import did nothing.
    /// </remarks>
    [Fact]
    public async Task Analyze_refuses_a_file_it_cannot_read()
    {
        var client = await ClientAsync("SuperAdmin");

        (await client.PostAsync("/api/import/analyze", Upload("not a spreadsheet", "notes.docx"),
                TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var noFile = new MultipartFormDataContent { { new StringContent("x"), "note" } };
        (await client.PostAsync("/api/import/analyze", noFile, TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "and a form with no file at all is not a file");
    }

    private static MultipartFormDataContent Upload(string content, string fileName)
    {
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(content));
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/csv");
        return new MultipartFormDataContent { { file, "file", fileName } };
    }
}
