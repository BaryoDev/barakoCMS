using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using barakoCMS.Models;
using static BarakoCMS.Tests.Features.Config.ConfigCallers;

namespace BarakoCMS.Tests.Features.Config;

/// <summary>
/// Sections a module registers: a form round trips through the Forms module's provider, and a
/// document in the older export shape, with its types and entries as arrays, applies through the
/// Portability module's section (#921), and the two routes it replaced are gone.
/// </summary>
[Collection("Integration1")]
public class ConfigModuleSectionsTests
{
    private readonly ConfigCallers _callers;

    public ConfigModuleSectionsTests(IntegrationTestFixture fixture) => _callers = new ConfigCallers(fixture);

    private static ContentTypeDefinition Enquiry(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        DisplayName = name,
        Fields =
        [
            new FieldDefinition { Name = "Name", DisplayName = "Name", Type = "string", IsRequired = true },
            new FieldDefinition { Name = "Email", DisplayName = "Email", Type = "email" },
        ],
    };

    private async Task<(string Source, string Target, string Type)> TwoTenantsWithTwoEntriesAsync()
    {
        var source = await _callers.TenantAsync();
        var target = await _callers.TenantAsync();
        var type = TestNames.Compact("member");
        await _callers.StoreInTenantAsync(source, Type(type, ("Title", "string")));
        var sourceClient = await _callers.SuperAdminAsync(source);
        foreach (var title in new[] { "Ana", "Ben" })
        {
            (await sourceClient.PostAsJsonAsync("/api/contents", new { contentType = type, status = "Published", data = new Dictionary<string, object> { ["Title"] = title } }, Ct))
                .StatusCode.Should().Be(HttpStatusCode.OK);
        }

        return (source, target, type);
    }

    [Fact]
    public async Task An_export_of_types_and_entries_applies_as_a_config_document()
    {
        var (source, target, type) = await TwoTenantsWithTwoEntriesAsync();
        var sourceClient = await _callers.SuperAdminAsync(source);

        var (exported, document) = await ExportAsync(sourceClient, "contentTypes,contents");
        exported.StatusCode.Should().Be(HttpStatusCode.OK, document.ToString());
        document.GetProperty("contents").EnumerateObject().Should().HaveCount(2);

        var targetClient = await _callers.SuperAdminAsync(target);
        var (planned, plan) = await PlanAsync(targetClient, document);
        planned.StatusCode.Should().Be(HttpStatusCode.OK, plan.ToString());
        Changes(plan).Should().Contain($"contentTypes/{type} create");
        Changes(plan).Count(c => c.StartsWith("contents/", StringComparison.Ordinal)).Should().Be(2, plan.ToString());

        var (applied, result) = await ApplyAsync(targetClient, document);
        applied.StatusCode.Should().Be(HttpStatusCode.OK, result.ToString());
        var titles = await _callers.InTenantAsync(target, s => s.Query<Content>().Where(c => c.ContentType == type).ToListAsync(Ct));
        titles!.Select(c => c.Data["Title"]?.ToString()).Should().BeEquivalentTo("Ana", "Ben");
    }

    [Fact]
    public async Task A_plan_accepts_the_older_export_shape_with_types_and_entries_as_arrays()
    {
        var (source, target, type) = await TwoTenantsWithTwoEntriesAsync();
        var (_, exported) = await ExportAsync(await _callers.SuperAdminAsync(source), "contentTypes,contents");

        // The shape the removed export route wrote: a version, and two arrays.
        var older = new
        {
            version = 1,
            // The removed route named each type inside its item, and the section export keys it instead.
            contentTypes = exported.GetProperty("contentTypes").EnumerateObject()
                .Select(p => p.Value.EnumerateObject()
                    .ToDictionary(f => f.Name, f => (object?)f.Value)
                    .Concat([new KeyValuePair<string, object?>("name", p.Name)])
                    .ToDictionary())
                .ToArray(),
            contents = exported.GetProperty("contents").EnumerateObject().Select(p => p.Value).ToArray(),
        };

        var (planned, plan) = await PlanAsync(await _callers.SuperAdminAsync(target), older);

        planned.StatusCode.Should().Be(HttpStatusCode.OK, plan.ToString());
        Changes(plan).Should().Contain($"contentTypes/{type} create");
        Changes(plan).Count(c => c.StartsWith("contents/", StringComparison.Ordinal)).Should().Be(2, plan.ToString());
        plan.GetProperty("errors").GetArrayLength().Should().Be(0, plan.ToString());
    }

    [Fact]
    public async Task A_content_types_export_with_types_names_only_those_types()
    {
        var tenant = await _callers.TenantAsync();
        var kept = TestNames.Compact("kept");
        var left = TestNames.Compact("left");
        await _callers.StoreInTenantAsync(tenant, Type(kept, ("Title", "string")), Type(left, ("Title", "string")));
        var client = await _callers.SuperAdminAsync(tenant);

        var (both, all) = await ExportAsync(client, "contentTypes", $"{kept},{left}");
        both.StatusCode.Should().Be(HttpStatusCode.OK, all.ToString());
        all.GetProperty("contentTypes").EnumerateObject().Select(t => t.Name.ToLowerInvariant())
            .Should().BeEquivalentTo(kept.ToLowerInvariant(), left.ToLowerInvariant(),
                "the control: naming both types returns both, so a one-type answer below is the narrowing and not a tenant that holds one");

        var (one, narrowed) = await ExportAsync(client, "contentTypes", kept);
        one.StatusCode.Should().Be(HttpStatusCode.OK, narrowed.ToString());
        narrowed.GetProperty("contentTypes").EnumerateObject().Select(t => t.Name.ToLowerInvariant())
            .Should().Equal([kept.ToLowerInvariant()],
                "types= names the types to export, so the other type the tenant holds must be left out");
    }

    [Fact]
    public async Task A_document_without_a_version_is_refused_with_the_documented_400()
    {
        var target = await _callers.TenantAsync();
        var client = await _callers.SuperAdminAsync(target);

        var response = await client.PostAsJsonAsync(
            "/api/config/apply",
            new { document = new { contentTypes = Array.Empty<object>(), contents = Array.Empty<object>() }, allowDestructive = false },
            Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("The document declares no version");
    }

    [Fact]
    public async Task The_removed_portability_routes_answer_404_for_an_admin()
    {
        var client = await _callers.SuperAdminAsync(await _callers.TenantAsync());

        (await client.GetAsync("/api/portability/export", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PostAsJsonAsync("/api/portability/import", new { contentTypes = Array.Empty<object>(), contents = Array.Empty<object>() }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Types_narrows_the_content_types_and_entries_and_leaves_another_section_whole()
    {
        var tenant = await _callers.TenantAsync();
        var kept = TestNames.Compact("kept");
        var left = TestNames.Compact("left");
        await _callers.StoreInTenantAsync(tenant, Type(kept, ("Title", "string")), Type(left, ("Title", "string")));
        var client = await _callers.SuperAdminAsync(tenant);
        foreach (var type in new[] { kept, left })
        {
            (await client.PostAsJsonAsync("/api/contents", new { contentType = type, status = "Published", data = new Dictionary<string, object> { ["Title"] = type } }, Ct))
                .StatusCode.Should().Be(HttpStatusCode.OK);
        }

        await _callers.StoreInTenantAsync(tenant, new Role { Id = Guid.NewGuid(), Name = TestNames.Compact("editor"), TenantSlug = tenant, SystemCapabilities = [] });

        var (_, everything) = await ExportAsync(client, "contentTypes,contents,roles");
        everything.GetProperty("contentTypes").EnumerateObject().Select(p => p.Name).Should().Contain([kept, left]);
        var roles = everything.GetProperty("roles").EnumerateObject().Select(p => p.Name).ToList();
        roles.Should().NotBeEmpty("the control: the tenant has a role of its own");

        // Case does not matter, and a name that matches no type is ignored.
        var (response, narrowed) = await ExportAsync(client, "contentTypes,contents,roles", $"{kept.ToUpperInvariant()},no-such-type");

        response.StatusCode.Should().Be(HttpStatusCode.OK, narrowed.ToString());
        narrowed.GetProperty("contentTypes").EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo([kept]);
        narrowed.GetProperty("contents").EnumerateObject()
            .Select(p => p.Value.GetProperty("contentType").GetString()).Should().OnlyContain(t => t == kept).And.HaveCount(1);
        narrowed.GetProperty("roles").EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(roles);
    }

    [Fact]
    public async Task Types_that_match_nothing_export_no_types_and_no_entries()
    {
        var tenant = await _callers.TenantAsync();
        await _callers.StoreInTenantAsync(tenant, Type(TestNames.Compact("some"), ("Title", "string")));
        var client = await _callers.SuperAdminAsync(tenant);

        var (response, body) = await ExportAsync(client, "contentTypes,contents", "no-such-type");

        response.StatusCode.Should().Be(HttpStatusCode.OK, body.ToString());
        body.GetProperty("contentTypes").EnumerateObject().Should().BeEmpty();
        body.GetProperty("contents").EnumerateObject().Should().BeEmpty();
    }

    /// <summary>
    /// A target tenant holding the type, and a document of the source's two entries alone, so what a
    /// caller needs for the <c>contents</c> section is not mixed up with what the types need.
    /// </summary>
    private async Task<(string Target, string Type, object Document)> EntriesOnlyDocumentAsync()
    {
        var (source, target, type) = await TwoTenantsWithTwoEntriesAsync();
        var (_, exported) = await ExportAsync(await _callers.SuperAdminAsync(source), "contents");
        await _callers.StoreInTenantAsync(target, Type(type, ("Title", "string")));
        return (target, type, new { version = 1, contents = exported.GetProperty("contents") });
    }

    private async Task<HttpClient> CallerWithAsync(string tenant, params string[] capabilities) =>
        await _callers.ClientAsync(tenant, [await _callers.RoleAsync(capabilities)]);

    [Fact]
    public async Task A_role_with_import_content_and_no_export_content_plans_and_applies_entries()
    {
        var (target, type, document) = await EntriesOnlyDocumentAsync();
        var importer = await CallerWithAsync(target, SystemCapabilities.ManageConfig, BarakoCMS.Portability.PortabilityCapabilities.ImportContent);

        var (planned, plan) = await PlanAsync(importer, document);

        planned.StatusCode.Should().Be(HttpStatusCode.OK, plan.ToString());
        plan.TryGetProperty("refusals", out var refusals).Should().BeTrue();
        refusals.GetArrayLength().Should().Be(0, plan.ToString());
        Changes(plan).Count(c => c.StartsWith("contents/", StringComparison.Ordinal)).Should().Be(2, plan.ToString());

        var (applied, result) = await ApplyAsync(importer, document);

        applied.StatusCode.Should().Be(HttpStatusCode.OK, result.ToString());
        var titles = await _callers.InTenantAsync(target, s => s.Query<Content>().Where(c => c.ContentType == type).ToListAsync(Ct));
        titles!.Select(c => c.Data["Title"]?.ToString()).Should().BeEquivalentTo("Ana", "Ben");
    }

    [Fact]
    public async Task A_role_with_import_content_and_no_export_content_cannot_export_entries()
    {
        var (target, _, _) = await EntriesOnlyDocumentAsync();
        var importer = await CallerWithAsync(target, SystemCapabilities.ManageConfig, BarakoCMS.Portability.PortabilityCapabilities.ImportContent);

        var (response, body) = await ExportAsync(importer, "contents");

        response.StatusCode.Should().Be(HttpStatusCode.OK, body.ToString());
        body.GetProperty("withheld").EnumerateArray().Select(e => e.GetString()).Should().Contain("contents");
        body.TryGetProperty("contents", out _).Should().BeFalse("a section the caller may not read is withheld, not exported");
    }

    [Fact]
    public async Task A_role_with_export_content_and_no_import_content_plans_entries_and_is_refused_the_apply()
    {
        var (target, type, document) = await EntriesOnlyDocumentAsync();
        var reader = await CallerWithAsync(target, SystemCapabilities.ManageConfig, BarakoCMS.Portability.PortabilityCapabilities.ExportContent);

        var (planned, plan) = await PlanAsync(reader, document);
        planned.StatusCode.Should().Be(HttpStatusCode.OK, plan.ToString());

        var (applied, result) = await ApplyAsync(reader, document);

        applied.StatusCode.Should().Be(HttpStatusCode.Forbidden, result.ToString());
        result.ToString().Should().Contain(BarakoCMS.Portability.PortabilityCapabilities.ImportContent);
        (await _callers.InTenantAsync(target, s => s.Query<Content>().Where(c => c.ContentType == type).CountAsync(Ct))).Should().Be(0);
    }

    [Fact]
    public async Task A_role_with_manage_config_only_is_refused_a_plan_of_entries_and_the_message_names_both_capabilities()
    {
        var (target, _, document) = await EntriesOnlyDocumentAsync();
        var bare = await CallerWithAsync(target, SystemCapabilities.ManageConfig);

        var (planned, plan) = await PlanAsync(bare, document);

        planned.StatusCode.Should().Be(HttpStatusCode.Forbidden, plan.ToString());
        plan.ToString().Should().Contain(
            "The 'contents' section needs the 'export_content' or 'import_content' capability, which this caller is missing.");
    }

    [Fact]
    public async Task A_section_that_shows_stored_values_still_takes_its_read_capability_to_apply()
    {
        var tenant = await _callers.TenantAsync();
        var writer = await CallerWithAsync(tenant, SystemCapabilities.ManageConfig, SystemCapabilities.ManageConnectors);

        var (applied, result) = await ApplyAsync(writer, new { version = 1, connectors = new Dictionary<string, object>() });

        applied.StatusCode.Should().Be(HttpStatusCode.Forbidden, result.ToString());
        result.ToString().Should().Contain(SystemCapabilities.ViewConnectors,
            "a connectors plan shows what is stored, so the section keeps both capabilities");
    }

    [Fact]
    public async Task Entries_are_exported_only_when_the_contents_section_is_named()
    {
        var tenant = await _callers.TenantAsync();
        var type = TestNames.Compact("note");
        await _callers.StoreInTenantAsync(tenant, Type(type, ("Title", "string")));
        var client = await _callers.SuperAdminAsync(tenant);
        (await client.PostAsJsonAsync("/api/contents", new { contentType = type, status = "Published", data = new Dictionary<string, object> { ["Title"] = "kept" } }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var (_, everything) = await ExportAsync(client);
        everything.TryGetProperty("contentTypes", out _).Should().BeTrue("the control: the default export carries configuration");
        everything.TryGetProperty("contents", out _).Should().BeFalse("entries are content, not configuration");

        var (_, named) = await ExportAsync(client, "contents");
        named.GetProperty("contents").EnumerateObject().Select(p => p.Value.GetProperty("contentType").GetString()).Should().Contain(type);
    }
}
