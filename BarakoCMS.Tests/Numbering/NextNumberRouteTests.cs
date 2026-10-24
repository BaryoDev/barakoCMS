using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Models;
using static BarakoCMS.Tests.Features.Config.ConfigCallers;

namespace BarakoCMS.Tests.Numbering;

[Collection("Integration3")]
public class NextNumberRouteTests(IntegrationTestFixture fixture)
{
    private readonly BarakoCMS.Tests.Features.Config.ConfigCallers callers = new(fixture);

    private async Task<(string Tenant, HttpClient Admin)> TenantWithRuleAsync(bool clientAllocated, string? template = null)
    {
        var tenant = await callers.TenantAsync();
        await callers.StoreInTenantAsync(tenant, Type("Customer", ("CustomerNo", "string")));
        var admin = await callers.SuperAdminAsync(tenant);
        (await admin.PutAsJsonAsync("/api/content-types/Customer/numbering", new
        {
            field = "CustomerNo", prefix = "P-", padding = 4, clientAllocated, seriesTemplate = template,
        }, Ct)).EnsureSuccessStatusCode();
        return (tenant, admin);
    }

    private static Task<HttpResponseMessage> NextAsync(HttpClient client, string type = "Customer") =>
        client.PostAsJsonAsync("/api/numbering/next", new { contentType = type }, Ct);

    [Fact]
    public async Task A_client_allocated_series_hands_out_the_next_number_each_call()
    {
        var (_, admin) = await TenantWithRuleAsync(clientAllocated: true);

        var first = await NextAsync(admin);
        var second = await NextAsync(admin);

        first.StatusCode.Should().Be(HttpStatusCode.OK, await first.Content.ReadAsStringAsync(Ct));
        JsonDocument.Parse(await first.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("number").GetString().Should().Be("P-0001");
        JsonDocument.Parse(await second.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("number").GetString().Should().Be("P-0002");
    }

    [Fact]
    public async Task A_series_without_the_flag_is_made_by_the_server()
    {
        var (_, admin) = await TenantWithRuleAsync(clientAllocated: false);

        var response = await NextAsync(admin);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("This number is made by the server.");
    }

    [Fact]
    public async Task A_caller_without_create_rights_is_refused()
    {
        var (tenant, _) = await TenantWithRuleAsync(clientAllocated: true);
        var role = await callers.TenantRoleAsync(tenant);
        var reader = await callers.ClientAsync(tenant, [], [role]);

        var response = await NextAsync(reader);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_unknown_type_is_not_found()
    {
        var (_, admin) = await TenantWithRuleAsync(clientAllocated: true);

        (await NextAsync(admin, "Nothing")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_branch_template_without_a_branch_is_refused()
    {
        var (_, admin) = await TenantWithRuleAsync(clientAllocated: true, template: "{branch}.customer");

        var response = await NextAsync(admin);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_name_that_matches_two_stored_types_neither_exactly_is_refused()
    {
        var (tenant, admin) = await TenantWithRuleAsync(clientAllocated: true);
        var store = fixture.Services.GetRequiredService<IDocumentStore>();
        await using var legacy = await LegacyNameIndex.DropAsync(store, async () =>
        {
            await using var cleanup = store.LightweightSession(tenant);
            cleanup.DeleteWhere<ContentTypeDefinition>(d => d.Name.ToLower() == "customer");
            await cleanup.SaveChangesAsync(CancellationToken.None);
        }, Ct);
        await callers.StoreInTenantAsync(tenant, Type("CUSTOMER", ("CustomerNo", "string")));
        await callers.StoreInTenantAsync(tenant, Type("customer", ("CustomerNo", "string")));

        var response = await NextAsync(admin, "Customer");
        response.StatusCode.Should().Be(HttpStatusCode.OK, "the exact spelling is stored, so it is not a guess");

        var mixed = await NextAsync(admin, "cUSTOMER");
        mixed.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
