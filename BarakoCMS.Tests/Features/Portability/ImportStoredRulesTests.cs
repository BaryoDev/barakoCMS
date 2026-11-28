using System.Net;
using System.Text.Json.Nodes;
using BarakoCMS.Tests.Features.Config;
using FluentAssertions;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Portability;

/// <summary>
/// A type saved before rules were checked may store a rule a save refuses today. A config apply does
/// not refuse a document over that rule when the target already stores it, and still checks every
/// field the document adds or changes.
/// </summary>
[Collection("Integration1")]
public class ImportStoredRulesTests
{
    private const string UnknownRule = "matches";

    private readonly ConfigCallers _callers;

    public ImportStoredRulesTests(IntegrationTestFixture fixture) => _callers = new ConfigCallers(fixture);

    private Task<string> TenantAsync() => _callers.TenantAsync();

    private Task<HttpClient> AdminOfAsync(string tenantSlug) => _callers.SuperAdminAsync(tenantSlug);

    /// <summary>
    /// Stored straight into the tenant, because no endpoint accepts this rule any more and the point
    /// is a type that was saved when one did.
    /// </summary>
    private async Task<string> StoredTypeWithAnUnknownRuleAsync(string tenantSlug)
    {
        var type = $"legacy{Guid.NewGuid():n}"[..14];

        await _callers.StoreInTenantAsync(tenantSlug, new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = type,
            DisplayName = "Legacy",
            Fields =
            [
                new FieldDefinition
                {
                    Name = "Code",
                    DisplayName = "Code",
                    Type = "string",
                    ValidationRules = new() { [UnknownRule] = "^[A-Z]+$", ["maxLength"] = 12 },
                },
            ],
        });

        return type;
    }

    /// <summary>The type as <c>GET /api/config/export</c> writes it, with its rules.</summary>
    private static async Task<JsonObject> ExportAsync(HttpClient client, string type)
    {
        var (exported, body) = await ConfigCallers.ExportAsync(client, "contentTypes");
        exported.StatusCode.Should().Be(HttpStatusCode.OK, body.ToString());

        var document = JsonNode.Parse(body.GetRawText())!.AsObject();

        // The rule has to be in the document, or the apply below proves nothing about it.
        document["contentTypes"]!.AsObject().Select(p => p.Key).Should().ContainSingle().Which.Should().Be(type);
        var fields = Fields(document, type);
        fields.Count.Should().Be(1);
        fields[0]!["validationRules"]!.AsObject().Select(r => r.Key).Should().HaveCount(2).And.Contain(UnknownRule);

        return document;
    }

    private static JsonArray Fields(JsonObject document, string type) => document["contentTypes"]![type]!["fields"]!.AsArray();

    private static async Task<(HttpStatusCode Status, string Body)> ImportAsync(HttpClient client, JsonObject document)
    {
        var (response, body) = await ConfigCallers.ApplyAsync(client, document);
        return (response.StatusCode, body.ToString());
    }

    [Fact]
    public async Task A_type_storing_an_unknown_rule_exports_and_applies_back_into_its_own_tenant()
    {
        var tenant = await TenantAsync();
        var type = await StoredTypeWithAnUnknownRuleAsync(tenant);
        var admin = await AdminOfAsync(tenant);

        var document = await ExportAsync(admin, type);
        var (status, body) = await ImportAsync(admin, document);

        status.Should().Be(HttpStatusCode.OK, body);

        var after = await ExportAsync(admin, type);
        Fields(after, type)[0]!["name"]!.GetValue<string>().Should().Be("Code");
    }

    [Fact]
    public async Task A_new_field_with_an_unknown_rule_is_refused_without_blaming_the_stored_one()
    {
        var tenant = await TenantAsync();
        var type = await StoredTypeWithAnUnknownRuleAsync(tenant);
        var admin = await AdminOfAsync(tenant);

        var document = await ExportAsync(admin, type);
        Fields(document, type).Add(JsonNode.Parse("""
            { "name": "Score", "displayName": "Score", "type": "int", "validationRules": { "maximum": 100 } }
            """));

        var (status, body) = await ImportAsync(admin, document);

        status.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("Score").And.Contain("maximum");
        body.Should().NotContain(UnknownRule, "the stored field is unchanged, so its rule is not this request's to answer for");
    }

    [Fact]
    public async Task A_stored_field_whose_rules_the_document_changes_is_checked()
    {
        var tenant = await TenantAsync();
        var type = await StoredTypeWithAnUnknownRuleAsync(tenant);
        var admin = await AdminOfAsync(tenant);

        var document = await ExportAsync(admin, type);
        Fields(document, type)[0]!["validationRules"]![UnknownRule] = "^[a-z]+$";

        var (status, body) = await ImportAsync(admin, document);

        status.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("Code").And.Contain(UnknownRule);
    }

    [Fact]
    public async Task A_stored_field_whose_type_the_document_changes_is_checked()
    {
        var tenant = await TenantAsync();
        var type = await StoredTypeWithAnUnknownRuleAsync(tenant);
        var admin = await AdminOfAsync(tenant);

        var document = await ExportAsync(admin, type);
        Fields(document, type)[0]!["type"] = "text";

        var (status, body) = await ImportAsync(admin, document);

        status.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("Code").And.Contain(UnknownRule);
    }

    [Fact]
    public async Task A_type_storing_an_unknown_rule_is_refused_by_a_tenant_that_does_not_have_it()
    {
        var source = await TenantAsync();
        var destination = await TenantAsync();
        var type = await StoredTypeWithAnUnknownRuleAsync(source);

        var document = await ExportAsync(await AdminOfAsync(source), type);
        var (status, body) = await ImportAsync(await AdminOfAsync(destination), document);

        status.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("Code").And.Contain(UnknownRule);
    }
}
