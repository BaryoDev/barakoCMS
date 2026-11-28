using System.Net;
using System.Text.Json.Nodes;
using BarakoCMS.Tests.Features.Config;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Portability;

/// <summary>
/// A money field's currency travels in a config document, and an apply does not change the one a
/// stored field declares: that goes through the currency endpoint, which counts the entries affected.
/// </summary>
[Collection("Integration1")]
public class ImportMoneyCurrencyTests
{
    private readonly IntegrationTestFixture _fixture;
    private readonly ConfigCallers _callers;

    public ImportMoneyCurrencyTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _callers = new ConfigCallers(fixture);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<string> TenantAsync() => _callers.TenantAsync();

    private Task<HttpClient> AdminOfAsync(string tenantSlug) => _callers.SuperAdminAsync(tenantSlug);

    private async Task<string> StoredTypeInDollarsAsync(string tenantSlug)
    {
        var type = $"invoice{Guid.NewGuid():n}"[..14];

        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession(tenantSlug);
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = type,
            DisplayName = "Invoice",
            Fields =
            [
                new FieldDefinition { Name = "Total", DisplayName = "Total", Type = "money", Currency = "USD" },
            ],
        });
        await session.SaveChangesAsync(Ct);

        return type;
    }

    private async Task<FieldDefinition> StoredTotalAsync(string tenantSlug, string type)
    {
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.QuerySession(tenantSlug);
        var def = await session.Query<ContentTypeDefinition>().SingleAsync(d => d.Name == type, Ct);
        return def.Fields.Single(f => f.Name == "Total");
    }

    /// <summary>The type as <c>GET /api/config/export</c> writes it, with no other type.</summary>
    private static async Task<JsonObject> ExportAsync(HttpClient client, string type)
    {
        var (exported, body) = await ConfigCallers.ExportAsync(client, "contentTypes", types: type);
        exported.StatusCode.Should().Be(HttpStatusCode.OK, body.ToString());

        var document = JsonNode.Parse(body.GetRawText())!.AsObject();

        // The currency has to be in the document, or the applies below prove nothing about it.
        document["contentTypes"]!.AsObject().Count.Should().Be(1);
        var fields = document["contentTypes"]![type]!["fields"]!.AsArray();
        fields.Count.Should().Be(1);
        fields[0]!["currency"]!.GetValue<string>().Should().Be("USD");

        return document;
    }

    private static JsonNode Total(JsonObject document, string type) => document["contentTypes"]![type]!["fields"]![0]!;

    private static async Task<(HttpStatusCode Status, string Body)> ImportAsync(HttpClient client, JsonObject document)
    {
        var (response, body) = await ConfigCallers.ApplyAsync(client, document);
        return (response.StatusCode, body.ToString());
    }

    [Fact]
    public async Task A_type_declaring_a_currency_exports_and_applies_back_with_it()
    {
        var tenant = await TenantAsync();
        var type = await StoredTypeInDollarsAsync(tenant);
        var admin = await AdminOfAsync(tenant);

        var document = await ExportAsync(admin, type);
        var (status, body) = await ImportAsync(admin, document);

        status.Should().Be(HttpStatusCode.OK, body);
        (await StoredTotalAsync(tenant, type)).Currency.Should().Be("USD");
    }

    [Fact]
    public async Task A_document_that_changes_a_stored_fields_currency_is_refused()
    {
        var tenant = await TenantAsync();
        var type = await StoredTypeInDollarsAsync(tenant);
        var admin = await AdminOfAsync(tenant);

        var document = await ExportAsync(admin, type);
        Total(document, type)["currency"] = "EUR";

        var (status, body) = await ImportAsync(admin, document);

        status.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("Total").And.Contain($"/api/content-types/{type}/fields/Total/currency");
        (await StoredTotalAsync(tenant, type)).Currency.Should().Be("USD");
    }

    [Fact]
    public async Task A_document_that_leaves_out_a_stored_fields_currency_is_refused_and_does_not_switch_it_off()
    {
        var tenant = await TenantAsync();
        var type = await StoredTypeInDollarsAsync(tenant);
        var admin = await AdminOfAsync(tenant);

        var document = await ExportAsync(admin, type);
        Total(document, type)["currency"] = null;

        var (status, body) = await ImportAsync(admin, document);

        status.Should().Be(HttpStatusCode.BadRequest, body);
        (await StoredTotalAsync(tenant, type)).Currency.Should().Be("USD");
    }

    [Fact]
    public async Task A_tenant_without_the_type_takes_the_documents_currency_and_holds_its_entries_to_it()
    {
        var source = await TenantAsync();
        var destination = await TenantAsync();
        var type = await StoredTypeInDollarsAsync(source);

        var document = await ExportAsync(await AdminOfAsync(source), type);
        document["contents"] = new JsonArray(new JsonObject
        {
            ["contentType"] = type,
            ["data"] = new JsonObject { ["Total"] = 10.005m },
        });

        var destinationAdmin = await AdminOfAsync(destination);
        var (refusedStatus, refused) = await ImportAsync(destinationAdmin, document);

        refusedStatus.Should().Be(HttpStatusCode.BadRequest, refused);
        refused.Should().Contain("Total").And.Contain("USD").And.Contain("2 decimal places");

        document["contents"]![0]!["data"]!["Total"] = 10.01m;
        var (status, body) = await ImportAsync(destinationAdmin, document);

        status.Should().Be(HttpStatusCode.OK, body);
        (await StoredTotalAsync(destination, type)).Currency.Should().Be("USD");
    }
}
