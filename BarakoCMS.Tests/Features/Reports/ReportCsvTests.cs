using System.Net;
using BarakoCMS.Reports;
using FluentAssertions;

namespace BarakoCMS.Tests.Features.Reports;

public class ReportCsvTests
{
    [Theory]
    [InlineData("=SUM(A1:A9)", "'=SUM(A1:A9)")]
    [InlineData("+1+1", "'+1+1")]
    [InlineData("-2+3", "'-2+3")]
    [InlineData("@cmd", "'@cmd")]
    [InlineData("\tpad", "'\tpad")]
    public void A_cell_that_starts_like_a_formula_gets_a_leading_apostrophe(string value, string expected)
    {
        ReportCsv.Cell(value).Should().Be(expected);
    }

    [Theory]
    [InlineData("north", "north")]
    [InlineData("a=b", "a=b")]
    [InlineData("", "")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    public void An_ordinary_cell_is_written_as_it_is_and_quoted_only_when_it_has_to_be(string value, string expected)
    {
        ReportCsv.Cell(value).Should().Be(expected);
    }

    [Fact]
    public void A_formula_that_also_holds_a_comma_is_defused_then_quoted()
    {
        ReportCsv.Cell("=HYPERLINK(\"x\",\"y\")").Should().Be("\"'=HYPERLINK(\"\"x\"\",\"\"y\"\")\"");
    }

    [Fact]
    public void A_written_run_defuses_a_group_key_and_ends_with_a_total_row()
    {
        var result = new ReportResult
        {
            Rows =
            [
                new ReportRow { Key = "=cmd|' /C calc'!A0", Count = 2, Measures = { ["sum_Amount"] = 7.5m } },
                new ReportRow { Key = "south", Count = 1, Measures = { ["sum_Amount"] = 1m } },
            ],
            Totals = new ReportRow { Count = 3, Measures = { ["sum_Amount"] = 8.5m } },
        };

        var lines = ReportCsv.Write("Region", ["sum_Amount"], result).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        lines.Should().HaveCount(4);
        lines[0].Should().Be("Region,count,sum_Amount");
        lines[1].Should().StartWith("'=cmd");
        lines[2].Should().Be("south,1,1");
        lines[3].Should().Be("Total,3,8.5");
    }
}

[Collection("Integration4")]
public class ReportCsvExportTests
{
    private readonly ReportTestSupport _support;

    public ReportCsvExportTests(IntegrationTestFixture factory) => _support = new ReportTestSupport(factory);

    [Fact]
    public async Task The_csv_export_escapes_a_group_key_a_visitor_typed()
    {
        var type = await _support.TypeAsync();
        var admin = await _support.SuperAdminAsync();
        await _support.StoreAsync(type, Guid.NewGuid(), "=1+1", 2m);

        var name = ReportTestSupport.NewName();
        await ReportTestSupport.SaveReportAsync(admin, name, new
        {
            name, contentType = type, groupBy = "Region",
            measures = new object[] { new { fn = "sum", field = "Amount" } },
        });

        var res = await admin.GetAsync($"/api/reports/{name}?format=csv");
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        res.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");

        var lines = (await res.Content.ReadAsStringAsync()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(3);
        lines[1].Should().StartWith("'=1+1,");
    }
}
