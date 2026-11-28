using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Features.ContentType.Blueprints;
using barakoCMS.Models;
using R = BarakoCMS.Tests.RollupProbe;

namespace BarakoCMS.Tests;

/// <summary>
/// The built-in inventory and attendance blueprints, driven the way a client drives them: apply the
/// blueprint, write entries through the content routes, let the rollup drain run and read the
/// values back. Every test works in a tenant of its own and creates its entries Published, since a
/// rollup counts Published entries only.
/// </summary>
/// <remarks>
/// The drain is run by the test, after the drain job a write queued is taken out of the queue, so
/// no read happens between a write and its recount by another test's job. An old time entry is made
/// old through its TimeIn: the clock a drain takes moves drain time only, never an entry's own
/// created time.
/// </remarks>
[Collection("Integration1")]
public class ReferenceSystemTests
{
    private const string Blueprints = "/api/content-types/blueprints";

    private readonly IntegrationTestFixture _fixture;

    public ReferenceSystemTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        FastEndpointsResolver.ClaimJobQueues(fixture.Services);
    }

    private static CancellationToken Ct => R.Ct;

    private async Task<(string Tenant, HttpClient Client)> TenantWithAsync(params string[] blueprints)
    {
        var (tenant, client) = await R.OwnTenantAsync(_fixture);
        foreach (var name in blueprints)
        {
            var applied = await client.PostAsync($"{Blueprints}/{name}", null, Ct);
            applied.StatusCode.Should().Be(HttpStatusCode.OK, await applied.Content.ReadAsStringAsync(Ct));
        }

        return (tenant, client);
    }

    private async Task DrainAsync(string tenant)
    {
        await R.DropQueuedDrainsAsync(_fixture, tenant);
        await R.DrainAsync(_fixture, tenant);
    }

    private static async Task<JsonElement> DataAsync(HttpClient client, Guid id)
    {
        var res = await client.GetAsync($"/api/contents/{id}", Ct);
        var body = await res.Content.ReadAsStringAsync(Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.GetProperty("data").Clone();
    }

    private static decimal? Num(JsonElement data, string field)
    {
        foreach (var property in data.EnumerateObject())
        {
            if (string.Equals(property.Name, field, StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.Number)
            {
                return property.Value.GetDecimal();
            }
        }

        return null;
    }

    private static async Task<Guid> NewProductAsync(HttpClient client, string name) =>
        await R.CreateAsync(client, "product", new() { ["Name"] = name, ["Sku"] = "sku-" + name, ["ReorderLevel"] = 2 });

    private static Task<Guid> NewMoveAsync(HttpClient client, Guid product, int qty, string reason) =>
        R.CreateAsync(client, "stock-move", new() { ["Product"] = product.ToString(), ["Qty"] = qty, ["Reason"] = reason });

    private static Task<HttpResponseMessage> CreateOrderAsync(HttpClient client, decimal total, params (Guid Product, int Qty, decimal Price)[] lines) =>
        client.PostAsJsonAsync(
            "/api/contents",
            new barakoCMS.Features.Content.Create.Request
            {
                ContentType = "sales-order",
                Data = new() { ["Total"] = total },
                Status = ContentStatus.Published,
                Lines = lines.Select(l => new Dictionary<string, object>
                {
                    ["Product"] = l.Product.ToString(), ["Qty"] = l.Qty, ["UnitPrice"] = l.Price,
                }).ToList(),
            },
            ApiJson.Options,
            Ct);

    [Fact]
    public async Task The_blueprint_list_names_inventory_and_attendance_with_no_errors()
    {
        var (_, client) = await R.OwnTenantAsync(_fixture);

        var res = await client.GetAsync(Blueprints, Ct);
        var body = await res.Content.ReadAsStringAsync(Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var items = JsonDocument.Parse(body).RootElement.GetProperty("items").EnumerateArray().ToList();

        foreach (var (name, types) in new[]
        {
            ("inventory", new[] { "product", "stock-move", "sales-order", "sales-order-line" }),
            ("attendance", new[] { "employee", "time-entry" }),
        })
        {
            var item = items.Single(i => i.GetProperty("name").GetString() == name);
            item.GetProperty("builtIn").GetBoolean().Should().BeTrue();
            item.GetProperty("errors").GetArrayLength().Should().Be(0, name);
            item.GetProperty("contentTypes").EnumerateArray().Select(t => t.GetString()).Should().Equal(types);
        }
    }

    [Fact]
    public async Task Stock_moves_of_plus_10_minus_3_and_plus_5_leave_12_on_hand_over_3_moves()
    {
        var (tenant, client) = await TenantWithAsync("inventory");
        var product = await NewProductAsync(client, "bolt");

        await NewMoveAsync(client, product, 10, "received");
        await NewMoveAsync(client, product, -3, "sold");
        await NewMoveAsync(client, product, 5, "adjusted");
        await DrainAsync(tenant);

        var data = await DataAsync(client, product);
        Num(data, "OnHand").Should().Be(12);
        Num(data, "MovesThisMonth").Should().Be(3);
    }

    [Fact]
    public async Task Moving_a_stock_move_to_another_product_recounts_both()
    {
        var (tenant, client) = await TenantWithAsync("inventory");
        var first = await NewProductAsync(client, "nut");
        var second = await NewProductAsync(client, "washer");
        var move = await NewMoveAsync(client, first, 10, "received");
        await NewMoveAsync(client, second, 5, "received");
        await DrainAsync(tenant);
        Num(await DataAsync(client, first), "OnHand").Should().Be(10);
        Num(await DataAsync(client, second), "OnHand").Should().Be(5);

        var moved = await client.PutAsJsonAsync(
            $"/api/contents/{move}",
            new barakoCMS.Features.Content.Update.Request
            {
                Id = move,
                Version = 0,
                Data = new() { ["Product"] = second.ToString(), ["Qty"] = 10, ["Reason"] = "received" },
            },
            ApiJson.Options,
            Ct);
        moved.StatusCode.Should().Be(HttpStatusCode.OK, await moved.Content.ReadAsStringAsync(Ct));
        await DrainAsync(tenant);

        var left = await DataAsync(client, first);
        Num(left, "OnHand").Should().Be(0);
        Num(left, "MovesThisMonth").Should().Be(0);
        var arrived = await DataAsync(client, second);
        Num(arrived, "OnHand").Should().Be(15);
        Num(arrived, "MovesThisMonth").Should().Be(2);
    }

    [Fact]
    public async Task A_sales_order_with_two_lines_and_the_matching_total_saves_and_reads_each_line_total()
    {
        var (_, client) = await TenantWithAsync("inventory");
        var bolt = await NewProductAsync(client, "bolt");
        var nut = await NewProductAsync(client, "nut");

        var created = await CreateOrderAsync(client, 27.50m, (bolt, 2, 10.00m), (nut, 3, 2.50m));

        var body = await created.Content.ReadAsStringAsync(Ct);
        created.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var id = JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid();

        var read = await client.GetAsync($"/api/contents/{id}/lines", Ct);
        var lines = JsonDocument.Parse(await read.Content.ReadAsStringAsync(Ct)).RootElement;
        lines.GetProperty("header").GetProperty("data").GetProperty("Total").GetDecimal().Should().Be(27.50m);
        lines.GetProperty("lines").EnumerateArray()
            .Select(l => Num(l.GetProperty("data"), "LineTotal"))
            .Should().Equal(20.00m, 7.50m);
    }

    [Fact]
    public async Task A_sales_order_whose_total_is_not_the_sum_of_its_lines_is_refused()
    {
        var (_, client) = await TenantWithAsync("inventory");
        var bolt = await NewProductAsync(client, "bolt");
        var nut = await NewProductAsync(client, "nut");

        var refused = await CreateOrderAsync(client, 27.40m, (bolt, 2, 10.00m), (nut, 3, 2.50m));

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await refused.Content.ReadAsStringAsync(Ct)).Should().Contain("lines add up to 27.50");
    }

    [Fact]
    public async Task A_time_entry_from_0800_to_1630_today_reads_8_and_a_half_hours_for_the_entry_and_the_employee()
    {
        var (tenant, client) = await TenantWithAsync("attendance");
        var employee = await R.CreateAsync(client, "employee", new() { ["Name"] = "Ana" });
        var today = DateTime.UtcNow.Date;

        var entry = await NewTimeEntryAsync(client, employee, today.AddHours(8), today.AddHours(16.5));
        await DrainAsync(tenant);

        Num(await DataAsync(client, entry), "Hours").Should().Be(8.5m);
        var data = await DataAsync(client, employee);
        Num(data, "HoursLast7Days").Should().Be(8.5m);
        Num(data, "EntriesThisMonth").Should().Be(1);
    }

    [Fact]
    public async Task A_time_entry_with_a_time_in_10_days_ago_is_left_out_of_the_last_7_days()
    {
        var (tenant, client) = await TenantWithAsync("attendance");
        var employee = await R.CreateAsync(client, "employee", new() { ["Name"] = "Ben" });
        var today = DateTime.UtcNow.Date;

        await NewTimeEntryAsync(client, employee, today.AddHours(8), today.AddHours(16.5));
        var old = await NewTimeEntryAsync(client, employee, today.AddDays(-10).AddHours(8), today.AddDays(-10).AddHours(12));
        await DrainAsync(tenant);

        Num(await DataAsync(client, old), "Hours").Should().Be(4m, "the old entry still has its own hours");
        Num(await DataAsync(client, employee), "HoursLast7Days").Should().Be(8.5m, "the old entry is outside the window");
    }

    [Fact]
    public async Task Blog_inventory_and_attendance_apply_into_one_tenant_with_no_clash()
    {
        var (_, client) = await TenantWithAsync("blog", "inventory", "attendance");

        var res = await client.GetAsync("/api/content-types?pageSize=100", Ct);
        var names = JsonDocument.Parse(await res.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("items")
            .EnumerateArray().Select(i => i.GetProperty("name").GetString()).ToList();
        names.Should().Contain([
            "post", "category", "author", "page",
            "product", "stock-move", "sales-order", "sales-order-line",
            "employee", "time-entry",
        ]);
    }

    [Fact]
    public async Task A_copy_of_inventory_with_a_sum_over_a_text_field_is_refused_on_apply()
    {
        var (_, client) = await R.OwnTenantAsync(_fixture);
        var dir = Path.Combine(Path.GetTempPath(), $"barako-reference-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var copy = InventoryCopy("inventory-text-sum");
            var qty = copy["contentTypes"]!.AsArray().Single(t => t!["name"]!.GetValue<string>() == "stock-move")!
                ["fields"]!.AsArray().Single(f => f!["name"]!.GetValue<string>() == "Qty")!;
            qty["type"] = "string";
            await File.WriteAllTextAsync(Path.Combine(dir, "inventory-text-sum.json"), copy.ToJsonString(), Ct);

            var host = _fixture.WithSetting("Blueprints:Path", dir);
            var custom = host.CreateClient();
            foreach (var header in client.DefaultRequestHeaders)
            {
                custom.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
            }

            var refused = await custom.PostAsync($"{Blueprints}/inventory-text-sum", null, Ct);

            var body = await refused.Content.ReadAsStringAsync(Ct);
            refused.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
            body.Should().Contain("OnHand").And.Contain("Qty");
            var types = await custom.GetAsync("/api/content-types?pageSize=100", Ct);
            (await types.Content.ReadAsStringAsync(Ct)).Should().NotContain("\"stock-move\"", "nothing was applied");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private JsonNode InventoryCopy(string name)
    {
        using var scope = _fixture.Services.CreateScope();
        var catalog = scope.ServiceProvider.GetRequiredService<BlueprintCatalog>();
        var json = JsonSerializer.Serialize(catalog.Find("inventory")!.Definition, BlueprintCatalog.Json);
        var copy = JsonNode.Parse(json)!;
        copy["name"] = name;
        return copy;
    }

    private static Task<Guid> NewTimeEntryAsync(HttpClient client, Guid employee, DateTime timeIn, DateTime timeOut) =>
        R.CreateAsync(client, "time-entry", new()
        {
            ["Employee"] = employee.ToString(),
            ["TimeIn"] = timeIn.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
            ["TimeOut"] = timeOut.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
        });
}
