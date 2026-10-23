using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Connectors;

/// <summary>
/// A saved query that groups, counts and sums in the database, under the reader's rules.
/// </summary>
/// <remarks>
/// Every figure asserted here is computed by hand from the seeded rows, and every set of rows is
/// built so a wrong grouping, a hidden row that leaks in or a filter that does nothing gives a
/// different number. Each refusal has a control beside it, so a runner that refused everything
/// cannot pass.
/// </remarks>
[Collection("Sequential")]
public class AggregateQueryTests
{
    private static int _ip;

    private readonly IntegrationTestFixture _factory;

    public AggregateQueryTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- the numbers ------------------------------------------------------------------------

    [Fact]
    public async Task Sums_counts_and_extremes_match_hand_computed_values_per_group()
    {
        var type = await SeedOrdersAsync(null,
            ("A", 10m), ("A", 20.5m), ("B", 5m), ("C", 100m), ("C", 1m), ("C", 1m));
        var client = await AdminAsync();

        var slug = await SaveOkAsync(client, type,
            groupBy: [new { field = "Customer" }],
            measures:
            [
                new { fn = "sum", field = "Total", @as = "total" },
                new { fn = "count", field = (string?)null, @as = "orders" },
                new { fn = "avg", field = "Total", @as = "average" },
                new { fn = "min", field = "Total", @as = "smallest" },
                new { fn = "max", field = "Total", @as = "largest" },
            ]);

        var body = await RunOkAsync(client, slug);
        var rows = body.GetProperty("rows").EnumerateArray().ToList();

        rows.Should().HaveCount(3, "three customers, so three groups");
        AssertRow(RowFor(rows, "Customer", "A"), 30.5m, 2, 15.25m, 10m, 20.5m);
        AssertRow(RowFor(rows, "Customer", "B"), 5m, 1, 5m, 5m, 5m);
        AssertRow(RowFor(rows, "Customer", "C"), 102m, 3, 34m, 1m, 100m);

        var totals = body.GetProperty("totals");
        totals.GetProperty("total").GetDecimal().Should().Be(137.5m);
        totals.GetProperty("orders").GetDecimal().Should().Be(6m);
        totals.GetProperty("smallest").GetDecimal().Should().Be(1m);
        totals.GetProperty("largest").GetDecimal().Should().Be(100m);
    }

    [Fact]
    public async Task A_filter_narrows_what_is_counted()
    {
        var type = await SeedOrdersAsync(null, ("A", 10m), ("A", 20m), ("B", 5m));
        var client = await AdminAsync();

        var slug = await SaveOkAsync(client, type,
            filters: [new { field = "Customer", op = "eq", value = "A" }],
            measures: [new { fn = "sum", field = "Total", @as = "total" }]);

        var body = await RunOkAsync(client, slug);

        body.GetProperty("totals").GetProperty("total").GetDecimal().Should().Be(30m,
            "only A's two orders are in, so 35 would mean the filter was ignored");
    }

    [Fact]
    public async Task Preview_of_an_aggregate_returns_the_rows_the_run_returns()
    {
        var type = await SeedOrdersAsync(null, ("A", 10m), ("B", 5m), ("B", 7m));
        var client = await AdminAsync();
        var slug = await SaveOkAsync(client, type,
            groupBy: [new { field = "Customer" }],
            measures: [new { fn = "sum", field = "Total", @as = "total" }]);

        var preview = await PostJsonAsync(client, $"/api/queries/{slug}/preview", new { });
        preview.GetProperty("ok").GetBoolean().Should().BeTrue(
            "got refusal: {0}", preview.TryGetProperty("refusal", out var r) ? r.ToString() : "none");

        var rows = preview.GetProperty("rows").EnumerateArray().ToList();
        rows.Should().HaveCount(2);
        RowFor(rows, "Customer", "B").GetProperty("total").GetDecimal().Should().Be(12m);
        preview.GetProperty("count").GetInt32().Should().Be(2, "count is the number of groups");
    }

    [Fact]
    public async Task A_saved_query_without_the_new_keys_still_saves_and_previews()
    {
        var type = await SeedOrdersAsync(null, ("A", 10m), ("B", 5m));
        var client = await AdminAsync();

        var res = await client.PostAsJsonAsync("/api/queries", new
        {
            name = "Plain", slug = NewSlug("q"), contentType = type,
            filters = Array.Empty<object>(), fields = new[] { "Customer", "Total" }, limit = 100,
        }, Ct);
        res.IsSuccessStatusCode.Should().BeTrue("got {0}", res.StatusCode);
        var slug = (await res.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("slug").GetString();

        var preview = await PostJsonAsync(client, $"/api/queries/{slug}/preview", new { });

        preview.GetProperty("ok").GetBoolean().Should().BeTrue();
        preview.GetProperty("count").GetInt32().Should().Be(2);
    }

    // ---- the reader's rules -----------------------------------------------------------------

    [Theory]
    [InlineData("group")]
    [InlineData("measure")]
    [InlineData("filter")]
    public async Task A_field_the_caller_may_not_read_is_refused_by_name_and_a_readable_one_runs(string use)
    {
        var type = await SeedOrdersAsync(null, ("A", 10m), ("B", 5m));
        var admin = await AdminAsync();

        var slug = use switch
        {
            "group" => await SaveOkAsync(admin, type, groupBy: [new { field = "Secret" }],
                measures: [new { fn = "count", field = (string?)null, @as = "n" }]),
            "measure" => await SaveOkAsync(admin, type,
                measures: [new { fn = "sum", field = "Secret", @as = "s" }]),
            _ => await SaveOkAsync(admin, type,
                filters: [new { field = "Secret", op = "gt", value = "0" }],
                measures: [new { fn = "count", field = (string?)null, @as = "n" }]),
        };
        var control = await SaveOkAsync(admin, type, groupBy: [new { field = "Customer" }],
            measures: [new { fn = "sum", field = "Total", @as = "total" }]);

        var restricted = await RestrictedAsync([type], conditions: null);

        var refused = await restricted.PostAsJsonAsync($"/api/queries/{slug}/run", new { }, Ct);
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await refused.Content.ReadAsStringAsync(Ct)).Should().Contain("Secret",
            "the field is named in the refusal, not ignored");

        var allowed = await RunOkAsync(restricted, control);
        allowed.GetProperty("rows").GetArrayLength().Should().Be(2, "the control: a readable field runs");
    }

    [Fact]
    public async Task A_field_inside_a_referenced_type_the_caller_may_not_read_is_refused_by_name()
    {
        var (invoice, line) = await SeedInvoicesAsync(null);
        var admin = await AdminAsync();
        var hidden = await SaveOkAsync(admin, invoice,
            measures: [new { fn = "sum", field = "Cost", @as = "c", via = "Lines" }]);
        var readable = await SaveOkAsync(admin, invoice,
            measures: [new { fn = "sum", field = "Amount", @as = "a", via = "Lines" }]);

        var restricted = await RestrictedAsync([invoice, line], conditions: null);

        var refused = await restricted.PostAsJsonAsync($"/api/queries/{hidden}/run", new { }, Ct);
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await refused.Content.ReadAsStringAsync(Ct)).Should().Contain("Cost");

        var body = await RunOkAsync(restricted, readable);
        body.GetProperty("totals").GetProperty("a").GetDecimal().Should().Be(35m, "the control: Amount is readable");
    }

    [Fact]
    public async Task A_row_the_callers_conditions_hide_is_not_counted()
    {
        var type = $"agg_{Guid.NewGuid():n}"[..14];
        await StoreAsync(null,
            OrderType(type),
            Order(type, "A", 10m, region: "north"), Order(type, "A", 20m, region: "north"),
            Order(type, "A", 1000m, region: "south"));
        var admin = await AdminAsync();
        var slug = await SaveOkAsync(admin, type, groupBy: [new { field = "Customer" }],
            measures:
            [
                new { fn = "sum", field = "Total", @as = "total" },
                new { fn = "count", field = (string?)null, @as = "orders" },
            ]);

        var all = await RunOkAsync(admin, slug);
        all.GetProperty("totals").GetProperty("total").GetDecimal().Should().Be(1030m,
            "the control: an administrator is not narrowed, so the south row counts");

        var north = await RestrictedAsync([type], new Dictionary<string, object>
        {
            ["Region"] = new Dictionary<string, object> { ["_eq"] = "north" },
        });
        var body = await RunOkAsync(north, slug);

        body.GetProperty("totals").GetProperty("total").GetDecimal().Should().Be(30m,
            "the south row is hidden by the caller's conditions, and counting it would make this 1030");
        body.GetProperty("totals").GetProperty("orders").GetDecimal().Should().Be(2m);
    }

    // ---- tenants and time -------------------------------------------------------------------

    [Fact]
    public async Task Another_tenants_entries_never_reach_a_total()
    {
        var acme = await TenantAsync();
        var globex = await TenantAsync();
        var type = $"agg_{Guid.NewGuid():n}"[..14];
        await StoreAsync(acme, OrderType(type), Order(type, "A", 10m), Order(type, "A", 5m));
        await StoreAsync(globex, OrderType(type), Order(type, "A", 100000m));

        var client = await MemberAsync(acme);
        var slug = await SaveOkAsync(client, type, groupBy: [new { field = "Customer" }],
            measures: [new { fn = "sum", field = "Total", @as = "total" }]);

        var body = await RunOkAsync(client, slug);
        var rows = body.GetProperty("rows").EnumerateArray().ToList();

        rows.Should().HaveCount(1);
        rows[0].GetProperty("total").GetDecimal().Should().Be(15m, "globex's 100000 is another tenant's");
    }

    [Fact]
    public async Task Day_buckets_follow_the_tenants_time_zone_not_UTC()
    {
        var tenant = await TenantAsync();
        var type = $"agg_{Guid.NewGuid():n}"[..14];
        // Manila is UTC+8: 20:00Z on the 10th is 04:00 on the 11th there, 10:00Z is 18:00 on the 10th.
        await StoreAsync(tenant,
            OrderType(type),
            Order(type, "A", 1m, date: "2026-03-10T20:00:00Z"),
            Order(type, "A", 2m, date: "2026-03-10T10:00:00Z"),
            new Content
            {
                Id = Guid.NewGuid(), ContentType = "site", Status = ContentStatus.Published,
                Data = new Dictionary<string, object> { ["TimeZone"] = "Asia/Manila" },
            });

        var client = await MemberAsync(tenant);
        var slug = await SaveOkAsync(client, type,
            groupBy: [new { field = "Date", bucket = "day" }],
            measures: [new { fn = "sum", field = "Total", @as = "total" }]);

        var rows = (await RunOkAsync(client, slug)).GetProperty("rows").EnumerateArray().ToList();

        rows.Should().HaveCount(2, "UTC would put both on the 10th and give one group");
        RowFor(rows, "Date", "2026-03-10").GetProperty("total").GetDecimal().Should().Be(2m);
        RowFor(rows, "Date", "2026-03-11").GetProperty("total").GetDecimal().Should().Be(1m);
    }

    [Fact]
    public async Task A_relative_range_keeps_only_the_months_it_names()
    {
        var now = DateTime.UtcNow;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var type = await SeedOrdersAsync(null,
            ("A", 10m, now.ToString("O")),
            ("A", 20m, monthStart.AddDays(-5).ToString("O")),
            ("A", 40m, monthStart.AddDays(-40).ToString("O")));
        var client = await AdminAsync();

        var thisMonth = await SaveOkAsync(client, type, range: new { field = "Date", relative = "thisMonth" },
            measures: [new { fn = "sum", field = "Total", @as = "total" }]);
        var lastMonth = await SaveOkAsync(client, type, range: new { field = "Date", relative = "lastMonth" },
            measures: [new { fn = "sum", field = "Total", @as = "total" }]);

        (await RunOkAsync(client, thisMonth)).GetProperty("totals").GetProperty("total").GetDecimal()
            .Should().Be(10m);
        (await RunOkAsync(client, lastMonth)).GetProperty("totals").GetProperty("total").GetDecimal()
            .Should().Be(20m, "five days before this month began is in last month, and 40 days before is not");
    }

    [Fact]
    public async Task Named_from_and_to_parameters_bound_the_range_at_run_time()
    {
        var type = await SeedOrdersAsync(null,
            ("A", 1m, "2026-03-01T12:00:00Z"),
            ("A", 2m, "2026-03-15T12:00:00Z"),
            ("A", 4m, "2026-04-02T12:00:00Z"));
        var client = await AdminAsync();
        var slug = await SaveOkAsync(client, type, range: new { field = "Date" },
            measures: [new { fn = "sum", field = "Total", @as = "total" }]);

        var march = await PostJsonAsync(client, $"/api/queries/{slug}/run", new { from = "2026-03-01", to = "2026-03-31" });
        march.GetProperty("totals").GetProperty("total").GetDecimal().Should().Be(3m, "the April entry is past the end");

        var fromOnly = await PostJsonAsync(client, $"/api/queries/{slug}/run", new { from = "2026-03-10" });
        fromOnly.GetProperty("totals").GetProperty("total").GetDecimal().Should().Be(6m, "the 1 March entry is before it");
    }

    [Fact]
    public async Task A_date_only_value_is_read_in_the_site_zone_for_buckets_and_range_edges()
    {
        var tenant = await TenantAsync();
        var type = $"agg_{Guid.NewGuid():n}"[..14];
        // Read as UTC midnight, 2026-03-11 is 17:00 on the 10th in Los Angeles and lands in the wrong day.
        await StoreAsync(tenant,
            OrderType(type),
            Order(type, "A", 1m, date: "2026-03-10"),
            Order(type, "A", 2m, date: "2026-03-11"),
            SiteWithZone("America/Los_Angeles"));

        var client = await MemberAsync(tenant);
        var byDay = await SaveOkAsync(client, type, groupBy: [new { field = "Date", bucket = "day" }],
            measures: [new { fn = "sum", field = "Total", @as = "total" }]);
        var ranged = await SaveOkAsync(client, type, range: new { field = "Date" },
            measures: [new { fn = "sum", field = "Total", @as = "total" }]);

        var rows = (await RunOkAsync(client, byDay)).GetProperty("rows").EnumerateArray().ToList();
        rows.Should().HaveCount(2);
        RowFor(rows, "Date", "2026-03-10").GetProperty("total").GetDecimal().Should().Be(1m);
        RowFor(rows, "Date", "2026-03-11").GetProperty("total").GetDecimal().Should().Be(2m);

        var edge = await PostJsonAsync(client, $"/api/queries/{ranged}/run", new { from = "2026-03-11", to = "2026-03-11" });
        edge.GetProperty("totals").GetProperty("total").GetDecimal().Should().Be(2m,
            "the 11th is inside its own day in the site zone, and 0 would mean it was read as UTC");
    }

    [Fact]
    public async Task A_malformed_stored_date_is_left_out_and_the_good_ones_still_count()
    {
        var tenant = await TenantAsync();
        var type = $"agg_{Guid.NewGuid():n}"[..14];
        await StoreAsync(tenant,
            OrderType(type),
            Order(type, "A", 1m, date: "2026-03-10"),
            Order(type, "A", 2m, date: "2026-03-10T09:30:00Z"),
            Order(type, "A", 100m, date: "2026-02-30"),
            Order(type, "A", 200m, date: "2026-03-10 trailing junk"),
            SiteWithZone("UTC"));

        var client = await MemberAsync(tenant);
        var slug = await SaveOkAsync(client, type, groupBy: [new { field = "Date", bucket = "day" }],
            range: new { field = "Date" },
            measures: [new { fn = "sum", field = "Total", @as = "total" }]);

        var body = await PostJsonAsync(client, $"/api/queries/{slug}/run", new { from = "2026-03-01", to = "2026-03-31" });
        var rows = body.GetProperty("rows").EnumerateArray().ToList();

        rows.Should().HaveCount(1);
        rows[0].GetProperty("total").GetDecimal().Should().Be(3m, "the two malformed values are not in any day");
    }

    [Fact]
    public async Task A_stored_offset_the_database_cannot_take_is_left_out_and_the_good_ones_still_count()
    {
        var tenant = await TenantAsync();
        var type = $"agg_{Guid.NewGuid():n}"[..14];
        await StoreAsync(tenant,
            OrderType(type),
            Order(type, "A", 1m, date: "2026-03-10T09:30:00+08:00"),
            Order(type, "A", 2m, date: "2026-03-10T09:30:00Z"),
            Order(type, "A", 100m, date: "2026-03-10T09:30:00+19:00"),
            SiteWithZone("UTC"));

        var client = await MemberAsync(tenant);
        var slug = await SaveOkAsync(client, type, groupBy: [new { field = "Date", bucket = "day" }],
            measures: [new { fn = "sum", field = "Total", @as = "total" }]);

        var rows = (await RunOkAsync(client, slug)).GetProperty("rows").EnumerateArray().ToList();

        rows.Should().HaveCount(1);
        rows[0].GetProperty("total").GetDecimal().Should().Be(3m, "the +19:00 value is not in any day");
    }

    [Fact]
    public async Task A_leap_day_is_counted()
    {
        var tenant = await TenantAsync();
        var type = $"agg_{Guid.NewGuid():n}"[..14];
        await StoreAsync(tenant,
            OrderType(type),
            Order(type, "A", 4m, date: "2024-02-29"),
            Order(type, "A", 8m, date: "2026-02-29"),
            SiteWithZone("UTC"));

        var client = await MemberAsync(tenant);
        var slug = await SaveOkAsync(client, type, groupBy: [new { field = "Date", bucket = "day" }],
            measures: [new { fn = "sum", field = "Total", @as = "total" }]);

        var rows = (await RunOkAsync(client, slug)).GetProperty("rows").EnumerateArray().ToList();

        rows.Should().HaveCount(1, "2026 has no 29 February");
        RowFor(rows, "Date", "2024-02-29").GetProperty("total").GetDecimal().Should().Be(4m);
    }

    [Fact]
    public async Task A_range_or_grouping_on_a_query_that_returns_rows_is_refused()
    {
        var type = await SeedOrdersAsync(null, ("A", 1m));
        var client = await AdminAsync();

        var withRange = await client.PostAsJsonAsync("/api/queries", new
        {
            name = "Rows", slug = NewSlug("q"), contentType = type, fields = new[] { "Customer" },
            range = new { field = "Date", relative = "thisMonth" },
        }, Ct);
        var withGroup = await client.PostAsJsonAsync("/api/queries", new
        {
            name = "Rows", slug = NewSlug("q"), contentType = type, fields = new[] { "Customer" },
            groupBy = new[] { new { field = "Customer" } },
        }, Ct);
        var control = await client.PostAsJsonAsync("/api/queries", new
        {
            name = "Rows", slug = NewSlug("q"), contentType = type, fields = new[] { "Customer" },
        }, Ct);

        withRange.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        withGroup.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        control.IsSuccessStatusCode.Should().BeTrue("the control: the same query without them saves");
    }

    [Theory]
    [InlineData("fields")]
    [InlineData("sortField")]
    [InlineData("limit")]
    public async Task Fields_a_sort_or_a_limit_on_an_aggregate_query_are_refused(string extra)
    {
        var type = await SeedOrdersAsync(null, ("A", 1m));
        var client = await AdminAsync();
        var body = new Dictionary<string, object?>
        {
            ["name"] = "Figures", ["slug"] = NewSlug("q"), ["contentType"] = type,
            ["measures"] = new[] { new { fn = "count", field = (string?)null, @as = "n" } },
        };
        body[extra] = extra switch { "fields" => new[] { "Customer" }, "sortField" => "Customer", _ => 5 };

        var res = await client.PostAsJsonAsync("/api/queries", body, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- references -------------------------------------------------------------------------

    [Fact]
    public async Task A_header_sums_its_lines_one_hop_away()
    {
        var (invoice, _) = await SeedInvoicesAsync(null);
        var client = await AdminAsync();
        var slug = await SaveOkAsync(client, invoice, groupBy: [new { field = "Customer" }],
            measures:
            [
                new { fn = "sum", field = "Amount", @as = "billed", via = "Lines" },
                new { fn = "count", field = (string?)null, @as = "lines", via = "Lines" },
            ]);

        var rows = (await RunOkAsync(client, slug)).GetProperty("rows").EnumerateArray().ToList();

        rows.Should().HaveCount(2);
        RowFor(rows, "Customer", "A").GetProperty("billed").GetDecimal().Should().Be(30m, "lines of 10 and 20");
        RowFor(rows, "Customer", "A").GetProperty("lines").GetDecimal().Should().Be(2m);
        RowFor(rows, "Customer", "B").GetProperty("billed").GetDecimal().Should().Be(5m);
    }

    [Fact]
    public async Task A_second_hop_is_refused()
    {
        var (invoice, _) = await SeedInvoicesAsync(null);
        var client = await AdminAsync();

        var res = await SaveAsync(client, invoice,
            measures: [new { fn = "sum", field = "Amount", @as = "a", via = "Lines.Parent" }]);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync(Ct)).Should().Contain("one hop");
    }

    // ---- bounds and hostile input -----------------------------------------------------------

    [Fact]
    public async Task Past_the_group_cap_the_query_is_refused_with_the_cap_in_the_message()
    {
        var type = $"agg_{Guid.NewGuid():n}"[..14];
        var docs = new List<object> { OrderType(type) };
        docs.AddRange(Enumerable.Range(0, 1001).Select(i => (object)Order(type, $"c{i:0000}", 1m)));
        await StoreAsync(null, docs.ToArray());
        var client = await AdminAsync();
        var slug = await SaveOkAsync(client, type, groupBy: [new { field = "Customer" }],
            measures: [new { fn = "count", field = (string?)null, @as = "n" }]);

        var res = await client.PostAsJsonAsync($"/api/queries/{slug}/run", new { }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, "1001 groups is one past the cap, and a cut-short answer is a wrong one");
        (await res.Content.ReadAsStringAsync(Ct)).Should().Contain("1000");
    }

    [Theory]
    [InlineData("Customer'; DROP TABLE x;--")]
    [InlineData("Customer->>'x'")]
    [InlineData("Cus\"tomer;")]
    public async Task A_field_name_holding_a_quote_an_arrow_or_a_semicolon_is_refused_and_nothing_runs(string hostile)
    {
        var type = await SeedOrdersAsync(null, ("A", 10m), ("B", 5m));
        var client = await AdminAsync();

        var res = await SaveAsync(client, type, groupBy: [new { field = hostile }],
            measures: [new { fn = "count", field = (string?)null, @as = "n" }]);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var control = await SaveOkAsync(client, type, groupBy: [new { field = "Customer" }],
            measures: [new { fn = "count", field = (string?)null, @as = "n" }]);
        (await RunOkAsync(client, control)).GetProperty("rows").GetArrayLength().Should().Be(2,
            "the type is intact and a normal query still runs");
    }

    [Fact]
    public async Task A_value_holding_a_quote_is_a_parameter_and_matches_nothing()
    {
        var type = await SeedOrdersAsync(null, ("A", 10m));
        var client = await AdminAsync();
        var slug = await SaveOkAsync(client, type,
            filters: [new { field = "Customer", op = "eq", value = "A'; DROP TABLE x;--" }],
            measures: [new { fn = "count", field = (string?)null, @as = "n" }]);

        var body = await RunOkAsync(client, slug);

        body.GetProperty("totals").GetProperty("n").GetDecimal().Should().Be(0m);
    }

    [Fact]
    public async Task Summing_a_text_field_is_refused()
    {
        var type = await SeedOrdersAsync(null, ("A", 10m));
        var client = await AdminAsync();

        var res = await SaveAsync(client, type, measures: [new { fn = "sum", field = "Customer", @as = "s" }]);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync(Ct)).Should().Contain("Customer");
    }

    // ---- review findings --------------------------------------------------------------------

    [Fact]
    public async Task Counting_a_field_that_is_not_a_number_is_refused_and_a_number_field_counts()
    {
        var type = await SeedOrdersAsync(null, ("A", 10m), ("B", 5m));
        var client = await AdminAsync();

        var refused = await SaveAsync(client, type, measures: [new { fn = "count", field = "Customer", @as = "n" }]);
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await refused.Content.ReadAsStringAsync(Ct)).Should().Contain("Customer");

        var slug = await SaveOkAsync(client, type, measures: [new { fn = "count", field = "Total", @as = "n" }]);
        (await RunOkAsync(client, slug)).GetProperty("totals").GetProperty("n").GetDecimal().Should().Be(2m);
    }

    [Fact]
    public async Task Totals_equal_the_sum_of_the_rows_when_a_date_is_unreadable_or_absent()
    {
        var tenant = await TenantAsync();
        var type = $"agg_{Guid.NewGuid():n}"[..14];
        var absent = Order(type, "A", 1000m);
        absent.Data.Remove("Date");
        await StoreAsync(tenant,
            OrderType(type),
            Order(type, "A", 1m, date: "2026-03-10"),
            Order(type, "A", 2m, date: "2026-03-11T09:30:00Z"),
            Order(type, "A", 100m, date: "2026-02-30"),
            absent,
            SiteWithZone("UTC"));

        var client = await MemberAsync(tenant);
        var slug = await SaveOkAsync(client, type, groupBy: [new { field = "Date", bucket = "day" }],
            measures: [new { fn = "sum", field = "Total", @as = "total" }, new { fn = "count", field = (string?)null, @as = "n" }]);

        var body = await RunOkAsync(client, slug);
        var rows = body.GetProperty("rows").EnumerateArray().ToList();

        rows.Should().HaveCount(2);
        rows.Sum(r => r.GetProperty("total").GetDecimal()).Should().Be(3m);
        body.GetProperty("totals").GetProperty("total").GetDecimal().Should().Be(3m,
            "1103 would mean the totals counted the records the rows left out");
        body.GetProperty("totals").GetProperty("n").GetDecimal().Should().Be(2m);
    }

    [Fact]
    public async Task A_run_time_from_or_to_on_a_query_with_no_range_is_refused()
    {
        var type = await SeedOrdersAsync(null, ("A", 1m));
        var client = await AdminAsync();
        var noRange = await SaveOkAsync(client, type, measures: [new { fn = "sum", field = "Total", @as = "t" }]);
        var ranged = await SaveOkAsync(client, type, range: new { field = "Date" },
            measures: [new { fn = "sum", field = "Total", @as = "t" }]);

        var refused = await client.PostAsJsonAsync($"/api/queries/{noRange}/run", new { from = "2026-03-01" }, Ct);
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await refused.Content.ReadAsStringAsync(Ct)).Should().Contain("no range");

        (await PostJsonAsync(client, $"/api/queries/{ranged}/run", new { from = "2000-02-01" }))
            .GetProperty("ok").GetBoolean().Should().BeTrue("the control: a saved range takes a run-time from");
    }

    [Fact]
    public async Task A_date_whose_next_day_overflows_is_refused_not_a_server_error()
    {
        var type = await SeedOrdersAsync(null, ("A", 1m));
        var client = await AdminAsync();
        var measures = new object[] { new { fn = "sum", field = "Total", @as = "t" } };

        var savedRange = await SaveAsync(client, type, range: new { field = "Date", to = "9999-12-31" }, measures: measures);
        savedRange.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var slug = await SaveOkAsync(client, type, range: new { field = "Date" }, measures: measures);
        var run = await client.PostAsJsonAsync($"/api/queries/{slug}/run", new { to = "9999-12-31" }, Ct);
        run.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await PostJsonAsync(client, $"/api/queries/{slug}/run", new { to = "9998-12-31" }))
            .GetProperty("ok").GetBoolean().Should().BeTrue("the control: the last supported day runs");
    }

    [Fact]
    public async Task A_week_bucket_is_the_iso_week_in_the_site_zone_labelled_by_its_monday()
    {
        var tenant = await TenantAsync();
        var type = $"agg_{Guid.NewGuid():n}"[..14];
        // Manila is UTC+8: 20:00Z on Sunday the 15th is 04:00 on Monday the 16th there.
        await StoreAsync(tenant,
            OrderType(type),
            Order(type, "A", 1m, date: "2026-03-09T00:30:00Z"),
            Order(type, "A", 2m, date: "2026-03-15T10:00:00Z"),
            Order(type, "A", 4m, date: "2026-03-15T20:00:00Z"),
            SiteWithZone("Asia/Manila"));

        var client = await MemberAsync(tenant);
        var slug = await SaveOkAsync(client, type, groupBy: [new { field = "Date", bucket = "week" }],
            measures: [new { fn = "sum", field = "Total", @as = "total" }]);

        var rows = (await RunOkAsync(client, slug)).GetProperty("rows").EnumerateArray().ToList();

        rows.Should().HaveCount(2, "UTC would put all three in the week of the 9th");
        RowFor(rows, "Date", "2026-03-09").GetProperty("total").GetDecimal().Should().Be(3m);
        RowFor(rows, "Date", "2026-03-16").GetProperty("total").GetDecimal().Should().Be(4m);
    }

    [Theory]
    [InlineData("last7days")]
    [InlineData("LAST7DAYS")]
    [InlineData("last7Days")]
    public async Task A_relative_range_name_is_matched_without_regard_to_case(string relative)
    {
        var now = DateTime.UtcNow;
        var type = await SeedOrdersAsync(null,
            ("A", 10m, now.ToString("O")),
            ("A", 40m, now.AddDays(-30).ToString("O")));
        var client = await AdminAsync();

        var slug = await SaveOkAsync(client, type, range: new { field = "Date", relative },
            measures: [new { fn = "sum", field = "Total", @as = "total" }]);

        (await RunOkAsync(client, slug)).GetProperty("totals").GetProperty("total").GetDecimal()
            .Should().Be(10m, "the entry 30 days back is outside the last seven days");
    }

    [Fact]
    public void A_timeout_is_told_from_other_database_errors()
    {
        barakoCMS.Features.Queries.Run.AggregateQuery.IsTimeout(new Npgsql.NpgsqlException("timeout", new TimeoutException())).Should().BeTrue();
        barakoCMS.Features.Queries.Run.AggregateQuery.IsTimeout(new Npgsql.PostgresException("canceled", "ERROR", "ERROR", "57014")).Should().BeTrue();
        barakoCMS.Features.Queries.Run.AggregateQuery.IsTimeout(new Npgsql.PostgresException("bad", "ERROR", "ERROR", "22023")).Should().BeFalse();
        barakoCMS.Features.Queries.Run.AggregateQuery.IsTimeout(new Npgsql.NpgsqlException("other", new InvalidOperationException())).Should().BeFalse();
    }

    [Fact]
    public async Task Lines_the_callers_conditions_hide_are_not_counted_through_a_reference()
    {
        var invoice = $"inv_{Guid.NewGuid():n}"[..14];
        var line = $"lin_{Guid.NewGuid():n}"[..14];
        Content NewLine(decimal amount, string region) => new()
        {
            Id = Guid.NewGuid(), ContentType = line, Status = ContentStatus.Published,
            Data = new Dictionary<string, object> { ["Amount"] = amount, ["Region"] = region },
        };

        var seen = NewLine(10m, "north");
        var hidden = NewLine(1000m, "south");
        FieldDefinition[] LineFields() =>
            [new() { Name = "Amount", Type = "decimal" }, new() { Name = "Region", Type = "string" }];
        await StoreAsync(null,
            new ContentTypeDefinition { Id = Guid.NewGuid(), Name = line, DisplayName = line, Fields = [.. LineFields()] },
            new ContentTypeDefinition
            {
                Id = Guid.NewGuid(), Name = invoice, DisplayName = invoice,
                Fields =
                [
                    new FieldDefinition { Name = "Region", Type = "string" },
                    new FieldDefinition { Name = "Lines", Type = "reference", ReferenceType = line, Multiple = true },
                ],
            },
            seen, hidden,
            new Content
            {
                Id = Guid.NewGuid(), ContentType = invoice, Status = ContentStatus.Published,
                Data = new Dictionary<string, object>
                {
                    ["Region"] = "north", ["Lines"] = new List<string> { seen.Id.ToString(), hidden.Id.ToString() },
                },
            });

        var admin = await AdminAsync();
        var slug = await SaveOkAsync(admin, invoice,
            measures:
            [
                new { fn = "sum", field = "Amount", @as = "billed", via = "Lines" },
                new { fn = "count", field = (string?)null, @as = "lines", via = "Lines" },
            ]);

        var all = await RunOkAsync(admin, slug);
        all.GetProperty("totals").GetProperty("billed").GetDecimal().Should().Be(1010m, "the control: an administrator sees both lines");

        var north = await RestrictedAsync([invoice, line], new Dictionary<string, object>
        {
            ["Region"] = new Dictionary<string, object> { ["_eq"] = "north" },
        });
        var body = await RunOkAsync(north, slug);

        body.GetProperty("totals").GetProperty("billed").GetDecimal().Should().Be(10m);
        body.GetProperty("totals").GetProperty("lines").GetDecimal().Should().Be(1m, "the south line is hidden from this caller");
    }

    [Theory]
    [InlineData("Bad;Name")]
    [InlineData("Bad->>Name")]
    [InlineData("Bad'Name")]
    [InlineData("Bad\"Name")]
    [InlineData("Customer2\n")]
    public async Task A_declared_field_whose_name_is_not_a_plain_name_is_refused_by_the_name_pattern(string hostile)
    {
        var type = $"agg_{Guid.NewGuid():n}"[..14];
        var definition = OrderType(type);
        definition.Fields.Add(new FieldDefinition { Name = hostile, Type = "decimal" });
        await StoreAsync(null, definition, Order(type, "A", 1m));
        var client = await AdminAsync();

        var grouped = await SaveAsync(client, type, groupBy: [new { field = hostile }],
            measures: [new { fn = "count", field = (string?)null, @as = "n" }]);
        grouped.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await grouped.Content.ReadAsStringAsync(Ct)).Should().Contain("cannot be grouped on",
            "the field is declared, so the name pattern refuses it, not the undeclared lookup");

        var measured = await SaveAsync(client, type, measures: [new { fn = "sum", field = hostile, @as = "s" }]);
        measured.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await measured.Content.ReadAsStringAsync(Ct)).Should().Contain("cannot be measured");
    }

    [Fact]
    public async Task A_measure_name_ending_in_a_newline_is_refused()
    {
        var type = await SeedOrdersAsync(null, ("A", 1m));
        var client = await AdminAsync();

        var res = await SaveAsync(client, type, measures: [new { fn = "count", field = (string?)null, @as = "n\n" }]);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync(Ct)).Should().Contain("needs a name");
    }

    [Fact]
    public async Task A_null_fields_list_on_an_aggregate_save_is_not_a_server_error()
    {
        var type = await SeedOrdersAsync(null, ("A", 1m));
        var client = await AdminAsync();

        var res = await client.PostAsJsonAsync("/api/queries", new
        {
            name = "Report", slug = NewSlug("q"), contentType = type, fields = (string[]?)null,
            measures = new[] { new { fn = "count", field = (string?)null, @as = "n" } },
        }, Ct);

        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", res.StatusCode, await res.Content.ReadAsStringAsync(Ct));
    }

    // ---- helpers ----------------------------------------------------------------------------

    private static void AssertRow(
        JsonElement row, decimal total, int orders, decimal average, decimal smallest, decimal largest)
    {
        row.GetProperty("total").GetDecimal().Should().Be(total);
        row.GetProperty("orders").GetDecimal().Should().Be(orders);
        row.GetProperty("average").GetDecimal().Should().Be(average);
        row.GetProperty("smallest").GetDecimal().Should().Be(smallest);
        row.GetProperty("largest").GetDecimal().Should().Be(largest);
    }

    private static string NewSlug(string prefix) => prefix + Guid.NewGuid().ToString("n")[..10];

    private static JsonElement RowFor(List<JsonElement> rows, string field, string key) =>
        rows.Single(r => r.GetProperty(field).GetString() == key);

    private async Task<HttpClient> AdminAsync()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await _factory.StoredUserTokenAsync("SuperAdmin", "Admin"));
        return client;
    }

    /// <summary>A caller who may manage queries and read only the types given, with no view_sensitive.</summary>
    private async Task<HttpClient> RestrictedAsync(string[] types, Dictionary<string, object>? conditions)
    {
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"agg_{Guid.NewGuid():n}",
            SystemCapabilities = [SystemCapabilities.ManageQueries],
            Permissions = types.Select(t => new ContentTypePermission
            {
                ContentTypeSlug = t,
                Read = new PermissionRule { Enabled = true, Conditions = conditions },
            }).ToList(),
        };
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = $"agg-{Guid.NewGuid():n}",
            Email = $"agg-{Guid.NewGuid():n}@example.com",
            RoleIds = [role.Id],
        };

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(role);
            session.Store(user);
            await session.SaveChangesAsync(Ct);
        }

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            _factory.CreateToken(["Restricted"], user.Id.ToString(),
                new Dictionary<string, string> { ["Username"] = user.Username }));
        return client;
    }

    private async Task<string> TenantAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var slug = $"agg-{Guid.NewGuid():N}"[..14].ToLowerInvariant();
        session.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug });
        await session.SaveChangesAsync(Ct);
        return slug;
    }

    private async Task<HttpClient> MemberAsync(string tenantSlug)
    {
        var userId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new User
            {
                Id = userId,
                Username = $"agg-{Guid.NewGuid():n}"[..14],
                Email = $"agg-{Guid.NewGuid():n}@example.com",
                RoleIds = [SystemRoles.SuperAdminRoleId],
            });
            session.Store(new Membership
            {
                Id = Guid.NewGuid(), UserId = userId, TenantSlug = tenantSlug,
                Status = MembershipStatus.Active, RoleIds = [SystemRoles.SuperAdminRoleId],
            });
            await session.SaveChangesAsync(Ct);
        }

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            _factory.CreateToken(["SuperAdmin"], userId.ToString(),
                new Dictionary<string, string> { ["tenant"] = tenantSlug }));
        client.DefaultRequestHeaders.Add("X-Tenant", tenantSlug);
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, $"198.51.100.{Interlocked.Increment(ref _ip) % 250 + 1}");
        return client;
    }

    private async Task StoreAsync(string? tenant, params object[] documents)
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        await using var session = tenant is null ? store.LightweightSession() : store.LightweightSession(tenant);
        foreach (var document in documents)
        {
            switch (document)
            {
                case ContentTypeDefinition definition: session.Store(definition); break;
                case Content content: session.Store(content); break;
                default: throw new ArgumentException($"Cannot store a {document.GetType().Name}.");
            }
        }

        await session.SaveChangesAsync(Ct);
    }

    private static Content SiteWithZone(string zone) => new()
    {
        Id = Guid.NewGuid(), ContentType = "site", Status = ContentStatus.Published,
        Data = new Dictionary<string, object> { ["TimeZone"] = zone },
    };

    private static ContentTypeDefinition OrderType(string name) => new()
    {
        Id = Guid.NewGuid(), Name = name, DisplayName = name,
        Fields =
        [
            new FieldDefinition { Name = "Customer", Type = "string" },
            new FieldDefinition { Name = "Region", Type = "string" },
            new FieldDefinition { Name = "Total", Type = "decimal" },
            new FieldDefinition { Name = "Date", Type = "datetime" },
            new FieldDefinition { Name = "Secret", Type = "decimal", Sensitivity = SensitivityLevel.Sensitive },
        ],
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static Content Order(string type, string customer, decimal total, string? region = null, string? date = null) => new()
    {
        Id = Guid.NewGuid(), ContentType = type, Status = ContentStatus.Published,
        Data = new Dictionary<string, object>
        {
            ["Customer"] = customer, ["Total"] = total, ["Region"] = region ?? "north",
            ["Date"] = date ?? DateTime.UtcNow.ToString("O"), ["Secret"] = 7m,
        },
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };

    private async Task<string> SeedOrdersAsync(string? tenant, params (string Customer, decimal Total)[] rows) =>
        await SeedOrdersAsync(tenant, rows.Select(r => (r.Customer, r.Total, (string?)null)).ToArray());

    private async Task<string> SeedOrdersAsync(string? tenant, params (string Customer, decimal Total, string? Date)[] rows)
    {
        var type = $"agg_{Guid.NewGuid():n}"[..14];
        var docs = new List<object> { OrderType(type) };
        docs.AddRange(rows.Select(r => (object)Order(type, r.Customer, r.Total, date: r.Date)));
        await StoreAsync(tenant, docs.ToArray());
        return type;
    }

    /// <summary>Invoice A holds lines of 10 and 20, invoice B one of 5. Cost on a line is Sensitive.</summary>
    private async Task<(string Invoice, string Line)> SeedInvoicesAsync(string? tenant)
    {
        var invoice = $"inv_{Guid.NewGuid():n}"[..14];
        var line = $"lin_{Guid.NewGuid():n}"[..14];

        Content NewLine(decimal amount) => new()
        {
            Id = Guid.NewGuid(), ContentType = line, Status = ContentStatus.Published,
            Data = new Dictionary<string, object> { ["Amount"] = amount, ["Cost"] = 1m },
        };

        Content NewInvoice(string customer, params Content[] lines) => new()
        {
            Id = Guid.NewGuid(), ContentType = invoice, Status = ContentStatus.Published,
            Data = new Dictionary<string, object>
            {
                ["Customer"] = customer,
                ["Lines"] = lines.Select(l => l.Id.ToString()).ToList(),
            },
        };

        var a1 = NewLine(10m);
        var a2 = NewLine(20m);
        var b1 = NewLine(5m);
        await StoreAsync(tenant,
            new ContentTypeDefinition
            {
                Id = Guid.NewGuid(), Name = line, DisplayName = line,
                Fields =
                [
                    new FieldDefinition { Name = "Amount", Type = "decimal" },
                    new FieldDefinition { Name = "Cost", Type = "decimal", Sensitivity = SensitivityLevel.Sensitive },
                    new FieldDefinition { Name = "Parent", Type = "reference", ReferenceType = invoice },
                ],
            },
            new ContentTypeDefinition
            {
                Id = Guid.NewGuid(), Name = invoice, DisplayName = invoice,
                Fields =
                [
                    new FieldDefinition { Name = "Customer", Type = "string" },
                    new FieldDefinition { Name = "Lines", Type = "reference", ReferenceType = line, Multiple = true },
                ],
            },
            a1, a2, b1, NewInvoice("A", a1, a2), NewInvoice("B", b1));
        return (invoice, line);
    }

    private Task<HttpResponseMessage> SaveAsync(
        HttpClient client, string type, object[]? filters = null, object[]? groupBy = null,
        object[]? measures = null, object? range = null, string? slug = null) =>
        client.PostAsJsonAsync("/api/queries", new
        {
            name = "Report", slug = slug ?? NewSlug("q"), contentType = type,
            filters = filters ?? [], fields = Array.Empty<string>(), limit = 100,
            groupBy, measures, range,
        }, Ct);

    private async Task<string> SaveOkAsync(
        HttpClient client, string type, object[]? filters = null, object[]? groupBy = null,
        object[]? measures = null, object? range = null)
    {
        var slug = NewSlug("q");
        var res = await SaveAsync(client, type, filters, groupBy, measures, range, slug);
        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", res.StatusCode, await res.Content.ReadAsStringAsync(Ct));
        return slug;
    }

    private async Task<JsonElement> RunOkAsync(HttpClient client, string slug) =>
        await PostJsonAsync(client, $"/api/queries/{slug}/run", new { });

    private async Task<JsonElement> PostJsonAsync(HttpClient client, string url, object body)
    {
        var res = await client.PostAsJsonAsync(url, body, Ct);
        var text = await res.Content.ReadAsStringAsync(Ct);
        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", res.StatusCode, text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }
}
