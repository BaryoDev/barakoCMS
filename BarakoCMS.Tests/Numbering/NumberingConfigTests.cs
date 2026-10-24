using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using barakoCMS.Models;
using static BarakoCMS.Tests.Features.Config.ConfigCallers;

namespace BarakoCMS.Tests.Numbering;

[Collection("Integration1")]
public class NumberingConfigTests(IntegrationTestFixture fixture)
{
    private readonly BarakoCMS.Tests.Features.Config.ConfigCallers callers = new(fixture);

    private async Task<(string Tenant, HttpClient Client)> CustomerTenantAsync()
    {
        var tenant = await callers.TenantAsync();
        await callers.StoreInTenantAsync(tenant, Type("Customer", ("CustomerNo", "string")));
        return (tenant, await callers.SuperAdminAsync(tenant));
    }

    private static object Rule(string? template = "{branch}.customer") => new
    {
        field = "CustomerNo", prefix = "P-", padding = 4, reset = "Never", strategy = "Gapless",
        seriesTemplate = template, clientAllocated = true,
    };

    [Fact]
    public async Task The_numbering_route_stores_the_template_and_the_flag()
    {
        var (_, client) = await CustomerTenantAsync();

        var put = await client.PutAsJsonAsync("/api/content-types/Customer/numbering", new
        {
            field = "CustomerNo", prefix = "P-", padding = 4, seriesTemplate = "{branch}.customer", clientAllocated = true,
        }, Ct);
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var list = JsonDocument.Parse(await client.GetStringAsync("/api/content-types", Ct)).RootElement;
        var type = list.GetProperty("items").EnumerateArray().Single(t => t.GetProperty("name").GetString() == "Customer");
        var numbering = type.GetProperty("numbering");
        numbering.GetProperty("seriesTemplate").GetString().Should().Be("{branch}.customer");
        numbering.GetProperty("clientAllocated").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task The_numbering_section_exports_the_rule_under_the_type_name()
    {
        var (_, client) = await CustomerTenantAsync();
        (await client.PutAsJsonAsync("/api/content-types/Customer/numbering", new
        {
            field = "CustomerNo", prefix = "P-", padding = 4, seriesTemplate = "{branch}.customer", clientAllocated = true,
        }, Ct)).EnsureSuccessStatusCode();

        var (response, body) = await ExportAsync(client, "numbering");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var customer = body.GetProperty("numbering").GetProperty("Customer");
        customer.GetProperty("field").GetString().Should().Be("CustomerNo");
        customer.GetProperty("prefix").GetString().Should().Be("P-");
        customer.GetProperty("padding").GetInt32().Should().Be(4);
        customer.GetProperty("reset").GetString().Should().Be("Never");
        customer.GetProperty("strategy").GetString().Should().Be("Gapless");
        customer.GetProperty("seriesTemplate").GetString().Should().Be("{branch}.customer");
        customer.GetProperty("clientAllocated").GetBoolean().Should().BeTrue();
        customer.TryGetProperty("timeZoneId", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Applying_a_numbering_document_sets_the_rule()
    {
        var (tenant, client) = await CustomerTenantAsync();

        var (response, _) = await ApplyAsync(client, new { version = 1, numbering = new { Customer = Rule() } });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var rule = await callers.InTenantAsync(tenant, async s =>
            (await s.Query<ContentTypeDefinition>().ToListAsync(Ct))
            .Single(d => d.Name == "Customer").Numbering);
        rule.Should().NotBeNull();
        rule!.SeriesTemplate.Should().Be("{branch}.customer");
        rule.ClientAllocated.Should().BeTrue();
        rule.Prefix.Should().Be("P-");
        rule.Padding.Should().Be(4);
    }

    [Fact]
    public async Task A_template_that_makes_a_bad_series_name_is_refused_on_apply()
    {
        var (_, client) = await CustomerTenantAsync();

        var (response, body) = await ApplyAsync(client, new { version = 1, numbering = new { Customer = Rule("{branch}/x") } });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body.ToString().Should().Contain("The series template makes a series name that is not valid.");
    }
}
