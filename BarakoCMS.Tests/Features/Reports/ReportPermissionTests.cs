using System.Net;
using System.Net.Http.Json;
using barakoCMS.Models;
using FluentAssertions;

namespace BarakoCMS.Tests.Features.Reports;

/// <summary>A report runs as the person who asks: it never shows more than their own read rules do.</summary>
[Collection("Sequential")]
public class ReportPermissionTests
{
    private readonly ReportTestSupport _support;

    public ReportPermissionTests(IntegrationTestFixture factory) => _support = new ReportTestSupport(factory);

    private static ContentTypePermission ReadOf(string type, params string[] fields) => new()
    {
        ContentTypeSlug = type,
        Read = new PermissionRule { Enabled = true, ReadableFields = fields.Length == 0 ? null : [.. fields] },
    };

    private async Task<string> SavedAsync(HttpClient admin, string type, object? groupBy, params object[] measures)
    {
        var name = ReportTestSupport.NewName();
        await ReportTestSupport.SaveReportAsync(admin, name, new
        {
            name, contentType = type, groupBy, measures,
        });
        return name;
    }

    [Fact]
    public async Task A_role_that_cannot_read_the_group_field_is_refused_and_one_that_can_gets_the_rows()
    {
        var type = await _support.TypeAsync();
        var admin = await _support.SuperAdminAsync();
        await _support.StoreAsync(type, Guid.NewGuid(), "north", 3m);
        var name = await SavedAsync(admin, type, "Region", new { fn = "count" });

        var (blind, _) = await _support.CallerAsync(ReadOf(type, "Amount"));
        var refused = await blind.GetAsync($"/api/reports/{name}");
        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden, await refused.Content.ReadAsStringAsync());

        var (sighted, _) = await _support.CallerAsync(ReadOf(type, "Region"));
        var allowed = await sighted.GetAsync($"/api/reports/{name}");
        allowed.StatusCode.Should().Be(HttpStatusCode.OK, await allowed.Content.ReadAsStringAsync());
        (await ReportTestSupport.JsonAsync(allowed)).GetProperty("rows").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task A_measure_on_a_sensitive_field_is_refused_to_a_role_that_cannot_read_it()
    {
        var type = await _support.TypeAsync();
        var admin = await _support.SuperAdminAsync();
        await _support.StoreAsync(type, Guid.NewGuid(), "north", 3m, salary: 9000m);
        var name = await SavedAsync(admin, type, "Region", new { fn = "sum", field = "Salary" });

        var (clerk, _) = await _support.CallerAsync(ReadOf(type, "Region", "Amount"));
        var refused = await clerk.GetAsync($"/api/reports/{name}");
        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden, await refused.Content.ReadAsStringAsync());
        (await refused.Content.ReadAsStringAsync()).Should().NotContain("9000");
    }

    [Fact]
    public async Task A_row_condition_limits_the_groups_and_the_totals_to_the_callers_own_entries()
    {
        var type = await _support.TypeAsync();
        var admin = await _support.SuperAdminAsync();
        var (reader, userId) = await _support.CallerAsync(new ContentTypePermission
        {
            ContentTypeSlug = type,
            Read = new PermissionRule
            {
                Enabled = true,
                Conditions = new Dictionary<string, object>
                {
                    ["$createdBy"] = new Dictionary<string, object> { ["_eq"] = "$CURRENT_USER" },
                },
            },
        });

        await _support.StoreAsync(type, userId, "mine", 2m);
        await _support.StoreAsync(type, userId, "mine", 3m);
        await _support.StoreAsync(type, Guid.NewGuid(), "theirs", 1000m);
        var name = await SavedAsync(admin, type, "Region", new { fn = "sum", field = "Amount" });

        var res = await reader.GetAsync($"/api/reports/{name}");
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var body = await ReportTestSupport.JsonAsync(res);

        var rows = body.GetProperty("rows").EnumerateArray().ToList();
        rows.Should().HaveCount(1);
        rows[0].GetProperty("key").GetString().Should().Be("mine");
        body.GetProperty("totals").GetProperty("count").GetInt64().Should().Be(2);
        body.GetProperty("totals").GetProperty("values").GetProperty("sum_Amount").GetDecimal().Should().Be(5m);
    }

    [Fact]
    public async Task A_role_with_no_read_permission_on_the_type_is_refused()
    {
        var type = await _support.TypeAsync();
        var admin = await _support.SuperAdminAsync();
        await _support.StoreAsync(type, Guid.NewGuid(), "north", 3m);
        var name = await SavedAsync(admin, type, "Region", new { fn = "count" });

        var (nobody, _) = await _support.CallerAsync();
        var res = await nobody.GetAsync($"/api/reports/{name}");
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Saving_a_report_needs_the_manage_reports_capability()
    {
        var type = await _support.TypeAsync();
        var (reader, _) = await _support.CallerAsync(ReadOf(type));

        var res = await reader.PostAsJsonAsync("/api/reports", new { name = ReportTestSupport.NewName(), contentType = type });
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, await res.Content.ReadAsStringAsync());
    }
}
