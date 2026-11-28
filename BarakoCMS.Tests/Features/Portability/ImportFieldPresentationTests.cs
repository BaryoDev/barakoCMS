using System.Net;
using System.Text.Json.Nodes;
using BarakoCMS.Tests.Features.Config;
using FluentAssertions;
using Marten;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Portability;

/// <summary>
/// A field's editor hint, section and role and a type's route template travel in a config document,
/// and an apply holds them to the rules the content type endpoints apply.
/// </summary>
[Collection("Integration1")]
public class ImportFieldPresentationTests
{
    private readonly ConfigCallers _callers;

    public ImportFieldPresentationTests(IntegrationTestFixture fixture) => _callers = new ConfigCallers(fixture);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<string> TenantAsync() => _callers.TenantAsync();

    private Task<HttpClient> AdminOfAsync(string tenantSlug) => _callers.SuperAdminAsync(tenantSlug);

    private static string NewType() => $"event{Guid.NewGuid():n}"[..14];

    private static List<FieldDefinition> HintedFields() =>
    [
        new() { Name = "EventName", DisplayName = "Event name", Type = "string", Role = "title", Section = "Details" },
        new() { Name = "Sections", DisplayName = "Sections", Type = "json", Editor = "blocks", Section = "Page" },
        new() { Name = "Slug", DisplayName = "Slug", Type = "slug" },
    ];

    private Task StoreTypeAsync(string tenantSlug, string type, string? routeTemplate, List<FieldDefinition> fields) =>
        _callers.StoreInTenantAsync(tenantSlug, new ContentTypeDefinition
        {
            Id = Guid.NewGuid(), Name = type, DisplayName = "Event", RouteTemplate = routeTemplate, Fields = fields,
        });

    private Task<ContentTypeDefinition?> StoredAsync(string tenantSlug, string type) =>
        _callers.InTenantAsync(tenantSlug,
            s => s.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name == type, Ct));

    /// <summary>The type as <c>GET /api/config/export</c> writes it, with no other type.</summary>
    private static async Task<JsonObject> ExportAsync(HttpClient client, string type)
    {
        var (exported, body) = await ConfigCallers.ExportAsync(client, "contentTypes", types: type);
        exported.StatusCode.Should().Be(HttpStatusCode.OK, body.ToString());

        var document = JsonNode.Parse(body.GetRawText())!.AsObject();
        document["contentTypes"]!.AsObject().Count.Should().Be(1);
        return document;
    }

    private static JsonObject TypeOf(JsonObject document, string type) => document["contentTypes"]![type]!.AsObject();

    private static JsonArray FieldsOf(JsonObject document, string type) => TypeOf(document, type)["fields"]!.AsArray();

    private static JsonObject FieldOf(JsonObject document, string type, string name) =>
        FieldsOf(document, type).Single(f => f!["name"]!.GetValue<string>() == name)!.AsObject();

    private static JsonNode FieldJson(FieldDefinition field) =>
        JsonNode.Parse(barakoCMS.Features.Config.ConfigJson.ToElement(field).GetRawText())!;

    private static async Task<(HttpStatusCode Status, string Body)> ImportAsync(HttpClient client, JsonObject document)
    {
        var (response, body) = await ConfigCallers.ApplyAsync(client, document);
        return (response.StatusCode, body.ToString());
    }

    [Fact]
    public async Task A_type_exports_its_hints_roles_and_route_template_and_another_tenant_applies_them()
    {
        var source = await TenantAsync();
        var destination = await TenantAsync();
        var type = NewType();
        await StoreTypeAsync(source, type, "/whats-on/{slug}", HintedFields());

        var document = await ExportAsync(await AdminOfAsync(source), type);

        // They have to be in the document, or the apply below proves nothing about them.
        TypeOf(document, type)["routeTemplate"]!.GetValue<string>().Should().Be("/whats-on/{slug}");
        FieldsOf(document, type).Should().HaveCount(3);
        FieldOf(document, type, "Sections")["editor"]!.GetValue<string>().Should().Be("blocks");
        FieldOf(document, type, "EventName")["role"]!.GetValue<string>().Should().Be("title");

        var (status, body) = await ImportAsync(await AdminOfAsync(destination), document);

        status.Should().Be(HttpStatusCode.OK, body);

        var landed = await StoredAsync(destination, type);
        landed.Should().NotBeNull();
        landed!.RouteTemplate.Should().Be("/whats-on/{slug}");
        landed.Fields.Should().HaveCount(3);

        var sections = landed.Fields.Single(f => f.Name == "Sections");
        sections.Editor.Should().Be("blocks");
        sections.Section.Should().Be("Page");

        var name = landed.Fields.Single(f => f.Name == "EventName");
        name.Role.Should().Be("title");
        name.Section.Should().Be("Details");
    }

    [Fact]
    public async Task Applying_over_a_stored_type_takes_the_documents_route_template_and_hints()
    {
        var tenant = await TenantAsync();
        var type = NewType();
        await StoreTypeAsync(tenant, type, "/old/{slug}",
        [
            new() { Name = "EventName", DisplayName = "Event name", Type = "string" },
            new() { Name = "Sections", DisplayName = "Sections", Type = "json" },
            new() { Name = "Slug", DisplayName = "Slug", Type = "slug" },
        ]);
        var admin = await AdminOfAsync(tenant);

        var document = await ExportAsync(admin, type);
        TypeOf(document, type)["routeTemplate"] = "/new/{slug}";
        TypeOf(document, type)["fields"] = new JsonArray(HintedFields().Select(FieldJson).ToArray());

        var (status, body) = await ImportAsync(admin, document);

        status.Should().Be(HttpStatusCode.OK, body);

        var stored = await StoredAsync(tenant, type);
        stored!.RouteTemplate.Should().Be("/new/{slug}");
        stored.Fields.Should().HaveCount(3);
        stored.Fields.Single(f => f.Name == "Sections").Editor.Should().Be("blocks");
        stored.Fields.Single(f => f.Name == "EventName").Role.Should().Be("title");
    }

    /// <summary>
    /// A document exported before these members existed carries none of them. Applied over a type
    /// that has since been given hints and a route template, it must leave them as stored.
    /// </summary>
    [Fact]
    public async Task A_document_that_carries_none_of_the_members_keeps_what_the_stored_type_holds()
    {
        var tenant = await TenantAsync();
        var type = NewType();
        await StoreTypeAsync(tenant, type, "/whats-on/{slug}", HintedFields());
        var admin = await AdminOfAsync(tenant);

        var document = await ExportAsync(admin, type);
        TypeOf(document, type).Remove("routeTemplate");
        FieldsOf(document, type).Should().HaveCount(3);
        foreach (var field in FieldsOf(document, type))
        {
            field!.AsObject().Remove("editor");
            field.AsObject().Remove("section");
            field.AsObject().Remove("role");
        }

        // Something the document does change, so a 200 that applied nothing cannot pass.
        FieldOf(document, type, "EventName")["displayName"] = "Name of the event";

        var (status, body) = await ImportAsync(admin, document);

        status.Should().Be(HttpStatusCode.OK, body);

        var stored = await StoredAsync(tenant, type);
        stored!.RouteTemplate.Should().Be("/whats-on/{slug}");
        stored.Fields.Should().HaveCount(3);

        var name = stored.Fields.Single(f => f.Name == "EventName");
        name.DisplayName.Should().Be("Name of the event");
        name.Role.Should().Be("title");
        name.Section.Should().Be("Details");

        var sections = stored.Fields.Single(f => f.Name == "Sections");
        sections.Editor.Should().Be("blocks");
        sections.Section.Should().Be("Page");
    }

    [Fact]
    public async Task A_document_can_move_a_role_to_another_field_and_a_changed_field_type_drops_the_stored_hint()
    {
        var tenant = await TenantAsync();
        var type = NewType();
        var fields = HintedFields();
        fields.Add(new FieldDefinition { Name = "Headline", DisplayName = "Headline", Type = "string" });
        await StoreTypeAsync(tenant, type, null, fields);
        var admin = await AdminOfAsync(tenant);

        var document = await ExportAsync(admin, type);
        FieldsOf(document, type).Should().HaveCount(4);
        FieldOf(document, type, "EventName").Remove("role");
        FieldOf(document, type, "Headline")["role"] = "title";

        var blocks = FieldOf(document, type, "Sections");
        blocks["type"] = "text";
        blocks.Remove("editor");

        var (status, body) = await ImportAsync(admin, document);

        status.Should().Be(HttpStatusCode.OK, body);

        var stored = await StoredAsync(tenant, type);
        stored!.Fields.Should().HaveCount(4);
        stored.Fields.Single(f => f.Name == "Headline").Role.Should().Be("title");
        stored.Fields.Single(f => f.Name == "EventName").Role.Should().BeNull();

        var sections = stored.Fields.Single(f => f.Name == "Sections");
        sections.Type.Should().Be("text");
        sections.Editor.Should().BeNull("the blocks editor is not for a text field, so it is not carried over");
    }

    [Fact]
    public async Task A_document_with_an_unknown_editor_a_repeated_role_or_a_bad_route_template_is_refused_and_stores_nothing()
    {
        var source = await TenantAsync();
        var destination = await TenantAsync();
        var type = NewType();
        await StoreTypeAsync(source, type, "/whats-on/{slug}", HintedFields());

        var exporter = await AdminOfAsync(source);
        var importer = await AdminOfAsync(destination);

        var unknownEditor = await ExportAsync(exporter, type);
        FieldOf(unknownEditor, type, "Sections")["editor"] = "kanban-board";
        var (firstStatus, firstBody) = await ImportAsync(importer, unknownEditor);
        firstStatus.Should().Be(HttpStatusCode.BadRequest, firstBody);
        firstBody.Should().Contain("Sections").And.Contain("Accepted values");
        firstBody.Should().NotContain("kanban-board");

        var repeatedRole = await ExportAsync(exporter, type);
        FieldsOf(repeatedRole, type).Add(FieldJson(
            new FieldDefinition { Name = "Headline", DisplayName = "Headline", Type = "string", Role = "title" }));
        var (secondStatus, secondBody) = await ImportAsync(importer, repeatedRole);
        secondStatus.Should().Be(HttpStatusCode.BadRequest, secondBody);
        secondBody.Should().Contain("EventName").And.Contain("Headline");

        var badRoute = await ExportAsync(exporter, type);
        TypeOf(badRoute, type)["routeTemplate"] = "@other.example/{slug}";
        var (thirdStatus, thirdBody) = await ImportAsync(importer, badRoute);
        thirdStatus.Should().Be(HttpStatusCode.BadRequest, thirdBody);
        thirdBody.Should().Contain("routeTemplate");

        (await StoredAsync(destination, type)).Should().BeNull();
    }

    /// <summary>
    /// A type stored over the field cap is validated a cap's worth of fields at a time, so two
    /// fields declaring one role can sit in different chunks.
    /// </summary>
    [Fact]
    public async Task A_role_declared_twice_is_refused_when_the_two_fields_are_validated_in_different_chunks()
    {
        var tenant = await TenantAsync();
        var type = NewType();
        var count = barakoCMS.Infrastructure.Services.ContentTypeFieldLimit.Default + 2;

        List<FieldDefinition> Fields() => Enumerable.Range(0, count)
            .Select(i => new FieldDefinition { Name = $"F{i:000}", DisplayName = $"F{i:000}", Type = "string" })
            .ToList();

        await StoreTypeAsync(tenant, type, null, Fields());
        var admin = await AdminOfAsync(tenant);

        var document = await ExportAsync(admin, type);
        FieldsOf(document, type).Should().HaveCount(count);
        FieldsOf(document, type)[0]!["role"] = "title";

        var (oneStatus, oneBody) = await ImportAsync(admin, document);
        oneStatus.Should().Be(HttpStatusCode.OK, "the control, one field holding the role applies: {0}", oneBody);

        FieldsOf(document, type)[count - 1]!["role"] = "title";
        var (twoStatus, twoBody) = await ImportAsync(admin, document);

        twoStatus.Should().Be(HttpStatusCode.BadRequest, twoBody);
        twoBody.Should().Contain("role");

        var stored = await StoredAsync(tenant, type);
        stored!.Fields.Should().HaveCount(count);
        stored.Fields.Count(f => f.Role == "title").Should().Be(1);
    }
}
