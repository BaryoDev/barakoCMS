using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace BarakoCMS.Tests.Features.Reports;

[Collection("Integration4")]
public class ReportRunTests
{
    private readonly ReportTestSupport _support;

    public ReportRunTests(IntegrationTestFixture factory) => _support = new ReportTestSupport(factory);

    [Fact]
    public async Task A_report_counts_and_sums_each_group_and_the_totals_cover_every_entry()
    {
        var type = await _support.TypeAsync();
        var admin = await _support.SuperAdminAsync();
        var owner = Guid.NewGuid();
        await _support.StoreAsync(type, owner, "north", 10.50m);
        await _support.StoreAsync(type, owner, "north", 4.50m);
        await _support.StoreAsync(type, owner, "south", 100m);

        var name = ReportTestSupport.NewName();
        await ReportTestSupport.SaveReportAsync(admin, name, new
        {
            name,
            contentType = type,
            groupBy = "Region",
            measures = new object[] { new { fn = "count" }, new { fn = "sum", field = "Amount" } },
        });

        var res = await admin.GetAsync($"/api/reports/{name}");
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var body = await ReportTestSupport.JsonAsync(res);

        var rows = body.GetProperty("rows").EnumerateArray().ToList();
        rows.Should().HaveCount(2);
        var north = rows.Single(r => r.GetProperty("key").GetString() == "north");
        north.GetProperty("count").GetInt64().Should().Be(2);
        north.GetProperty("values").GetProperty("sum_Amount").GetDecimal().Should().Be(15.00m);
        var south = rows.Single(r => r.GetProperty("key").GetString() == "south");
        south.GetProperty("count").GetInt64().Should().Be(1);
        south.GetProperty("values").GetProperty("sum_Amount").GetDecimal().Should().Be(100m);

        var totals = body.GetProperty("totals");
        totals.GetProperty("count").GetInt64().Should().Be(3);
        totals.GetProperty("values").GetProperty("sum_Amount").GetDecimal().Should().Be(115.00m);
        body.GetProperty("truncated").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task A_date_range_leaves_out_entries_created_outside_it()
    {
        var type = await _support.TypeAsync();
        var admin = await _support.SuperAdminAsync();
        var owner = Guid.NewGuid();
        await _support.StoreAsync(type, owner, "a", 1m, created: new DateTime(2026, 1, 10, 12, 0, 0, DateTimeKind.Utc));
        await _support.StoreAsync(type, owner, "a", 2m, created: new DateTime(2026, 2, 10, 12, 0, 0, DateTimeKind.Utc));
        await _support.StoreAsync(type, owner, "a", 4m, created: new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc));

        var name = ReportTestSupport.NewName();
        await ReportTestSupport.SaveReportAsync(admin, name, new
        {
            name, contentType = type, groupBy = "Region",
            measures = new object[] { new { fn = "sum", field = "Amount" } },
        });

        var res = await admin.GetAsync($"/api/reports/{name}?from=2026-02-01&to=2026-02-28");
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var body = await ReportTestSupport.JsonAsync(res);

        body.GetProperty("rows").GetArrayLength().Should().Be(1);
        body.GetProperty("totals").GetProperty("count").GetInt64().Should().Be(1);
        body.GetProperty("totals").GetProperty("values").GetProperty("sum_Amount").GetDecimal().Should().Be(2m);
    }

    [Fact]
    public async Task A_group_limit_returns_the_first_groups_marks_the_run_truncated_and_totals_every_group()
    {
        var type = await _support.TypeAsync();
        var admin = await _support.SuperAdminAsync();
        var owner = Guid.NewGuid();
        foreach (var region in new[] { "a", "b", "c" })
            await _support.StoreAsync(type, owner, region, 1m);

        var name = ReportTestSupport.NewName();
        await ReportTestSupport.SaveReportAsync(admin, name, new
        {
            name, contentType = type, groupBy = "Region", groupLimit = 2,
            measures = new object[] { new { fn = "count" } },
        });

        var res = await admin.GetAsync($"/api/reports/{name}");
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var body = await ReportTestSupport.JsonAsync(res);

        body.GetProperty("rows").GetArrayLength().Should().Be(2);
        body.GetProperty("truncated").GetBoolean().Should().BeTrue();
        body.GetProperty("totals").GetProperty("count").GetInt64().Should().Be(3);
    }

    [Fact]
    public async Task A_filter_limits_what_is_counted()
    {
        var type = await _support.TypeAsync();
        var admin = await _support.SuperAdminAsync();
        var owner = Guid.NewGuid();
        await _support.StoreAsync(type, owner, "a", 5m);
        await _support.StoreAsync(type, owner, "a", 50m);
        await _support.StoreAsync(type, owner, "b", 500m);

        var name = ReportTestSupport.NewName();
        await ReportTestSupport.SaveReportAsync(admin, name, new
        {
            name, contentType = type, groupBy = "Region",
            filters = new object[] { new { field = "Amount", op = "gte", value = "40" } },
            measures = new object[] { new { fn = "sum", field = "Amount" } },
        });

        var body = await ReportTestSupport.JsonAsync(await admin.GetAsync($"/api/reports/{name}"));
        body.GetProperty("totals").GetProperty("count").GetInt64().Should().Be(2);
        body.GetProperty("totals").GetProperty("values").GetProperty("sum_Amount").GetDecimal().Should().Be(550m);
    }

    [Fact]
    public async Task A_report_that_does_not_exist_is_not_found_and_a_name_cannot_be_saved_twice()
    {
        var type = await _support.TypeAsync();
        var admin = await _support.SuperAdminAsync();

        (await admin.GetAsync($"/api/reports/{ReportTestSupport.NewName()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var name = ReportTestSupport.NewName();
        var definition = new { name, contentType = type, groupBy = "Region" };
        await ReportTestSupport.SaveReportAsync(admin, name, definition);
        (await admin.PostAsJsonAsync("/api/reports", definition)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_numeric_filter_with_a_value_that_is_not_a_number_is_refused_when_saved()
    {
        var type = await _support.TypeAsync();
        var admin = await _support.SuperAdminAsync();
        var name = ReportTestSupport.NewName();

        var res = await admin.PostAsJsonAsync("/api/reports", new
        {
            name, contentType = type, groupBy = "Region",
            filters = new[] { new { field = "Amount", op = "gt", value = "lots" } },
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_run_to_the_last_representable_day_answers_instead_of_failing()
    {
        var type = await _support.TypeAsync();
        var admin = await _support.SuperAdminAsync();
        await _support.StoreAsync(type, Guid.NewGuid(), "north", 3m);
        var name = ReportTestSupport.NewName();
        await ReportTestSupport.SaveReportAsync(admin, name, new { name, contentType = type, groupBy = "Region" });

        var res = await admin.GetAsync($"/api/reports/{name}?to=9999-12-31");

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var body = await ReportTestSupport.JsonAsync(res);
        body.GetProperty("rows").EnumerateArray().Should().HaveCount(1);
    }
}
