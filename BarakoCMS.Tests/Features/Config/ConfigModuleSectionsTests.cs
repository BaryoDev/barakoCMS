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
            contentTypes = exported.GetProperty("contentTypes").EnumerateObject().Select(p => p.Value).ToArray(),
            contents = exported.GetProperty("contents").EnumerateObject().Select(p => p.Value).ToArray(),
        };

        var (planned, plan) = await PlanAsync(await _callers.SuperAdminAsync(target), older);

        planned.StatusCode.Should().Be(HttpStatusCode.OK, plan.ToString());
        Changes(plan).Should().Contain($"contentTypes/{type} create");
        Changes(plan).Count(c => c.StartsWith("contents/", StringComparison.Ordinal)).Should().Be(2, plan.ToString());
        plan.GetProperty("errors").GetArrayLength().Should().Be(0, plan.ToString());
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
