using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BarakoCMS.Tests.Features.Config;
using barakoCMS.Data;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
using static BarakoCMS.Tests.Features.Config.ConfigCallers;

namespace BarakoCMS.Tests.Features.Connectors;

/// <summary>
/// Grants in config as code: the plan and the apply judge them, an export carries them only to a
/// manager, a seed file applies them as the host, and two tenants keep their own (#1423).
/// </summary>
[Collection("Integration1")]
public class ConnectorGrantConfigTests(IntegrationTestFixture fixture)
{
    private const string Home = "https://crm.example.com/v1";

    private readonly ConfigCallers _callers = new(fixture);

    private sealed class HostShim(IServiceProvider services) : IHost
    {
        public IServiceProvider Services { get; } = services;

        public void Dispose() { }

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static FieldDefinition Field(string name, SensitivityLevel level = SensitivityLevel.Public) =>
        new() { Name = name, DisplayName = name, Type = "string", Sensitivity = level };

    private static ContentTypeDefinition TypeOf(string name, params FieldDefinition[] fields) => new()
    {
        Id = Guid.NewGuid(), Name = name, DisplayName = name, Fields = [.. fields],
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static Connector Linked(string slug, params string[] grants) => new()
    {
        Id = Guid.NewGuid(), Name = "Link", Slug = slug, BaseUrl = Home, Auth = ConnectorAuth.None,
        Enabled = true, ProbePath = "/", AllowedFields = grants.Length == 0 ? null : [.. grants],
    };

    /// <summary>A tenant that holds a type with a Public Title and a Sensitive Ssn.</summary>
    private async Task<(string Tenant, string Type)> TenantWithTypeAsync()
    {
        var tenant = await _callers.TenantAsync();
        var type = TestNames.Compact("grp");
        await _callers.StoreInTenantAsync(tenant, TypeOf(type, Field("Title"), Field("Ssn", SensitivityLevel.Sensitive)));
        return (tenant, type);
    }

    /// <summary>A signed-in caller in <paramref name="tenant"/> who holds these capabilities and no more.</summary>
    private async Task<HttpClient> HoldingAsync(string tenant, params string[] capabilities) =>
        await _callers.ClientAsync(tenant, [await _callers.RoleAsync(capabilities)]);

    private static object Document(string slug, string baseUrl, string[]? allowed, string auth = "None", string[]? secrets = null)
    {
        var connector = new Dictionary<string, object?>
        {
            ["name"] = "Link", ["baseUrl"] = baseUrl, ["auth"] = auth, ["enabled"] = true, ["secrets"] = secrets ?? [],
        };
        if (allowed is not null) connector["allowedFields"] = allowed;
        return new { version = 1, connectors = new Dictionary<string, object?> { [slug] = connector } };
    }

    private async Task<Connector> StoredAsync(string tenant, string slug) =>
        (await _callers.InTenantAsync(tenant, s => s.Query<Connector>().SingleAsync(c => c.Slug == slug, Ct)))!;

    private async Task<List<AuditEvent>> AuditAsync(string slug)
    {
        var store = fixture.Services.GetRequiredService<IDocumentStore>();
        var rows = new Dictionary<Guid, AuditEvent>();
        await using (var session = store.QuerySession())
        {
            foreach (var row in await session.Query<AuditEvent>().Where(e => e.Action.StartsWith("connector.")).ToListAsync(Ct))
            {
                rows[row.Id] = row;
            }
        }

        return rows.Values.Where(e => e.Metadata != null && e.Metadata["slug"].ToString() == slug).OrderBy(e => e.CreatedAt).ToList();
    }

    private static string[]? GrantsIn(JsonElement connector) =>
        connector.TryGetProperty("allowedFields", out var list) && list.ValueKind == JsonValueKind.Array
            ? [.. list.EnumerateArray().Select(e => e.GetString()!)]
            : null;

    [Fact]
    public async Task A_config_apply_by_a_user_audits_the_user_and_the_list()
    {
        var (tenant, type) = await TenantWithTypeAsync();
        var slug = TestNames.Slug("cg");

        var (response, body) = await ApplyAsync(await _callers.SuperAdminAsync(tenant), Document(slug, Home, [$"{type}.Ssn"]));

        response.StatusCode.Should().Be(HttpStatusCode.OK, body.ToString());
        (await StoredAsync(tenant, slug)).AllowedFields.Should().Equal($"{type}.Ssn");
        var row = (await AuditAsync(slug)).Should().ContainSingle(e => e.Action == "connector.created").Subject;
        row.ActorUserId.Should().NotBeNull("an apply through the API is the signed-in user's");
        row.ActorUsername.Should().StartWith("cfg-");
        row.Metadata!["allowedFieldsAfter"].ToString().Should().Be($"{type}.Ssn");
    }

    [Fact]
    public async Task A_plan_reports_a_grant_that_names_nothing_and_an_apply_writes_nothing()
    {
        var (tenant, _) = await TenantWithTypeAsync();
        var slug = TestNames.Slug("cg");
        var client = await _callers.SuperAdminAsync(tenant);
        var document = Document(slug, Home, ["nosuchtype.Ssn"]);

        var (_, plan) = await PlanAsync(client, document);
        var (applied, body) = await ApplyAsync(client, document);

        plan.ToString().Should().Contain("names a content type that does not exist");
        applied.IsSuccessStatusCode.Should().BeFalse(body.ToString());
        body.ToString().Should().Contain("names a content type that does not exist");
        (await _callers.InTenantAsync(tenant, s => s.Query<Connector>().AnyAsync(c => c.Slug == slug, Ct))).Should().BeFalse();
    }

    [Fact]
    public async Task A_config_apply_that_adds_a_grant_is_refused_while_a_field_change_runs_on_the_type_and_a_plan_is_not()
    {
        var (tenant, type) = await TenantWithTypeAsync();
        await _callers.StoreInTenantAsync(tenant, new FieldChange
        {
            Id = Guid.NewGuid(), ContentType = type, Field = "Title", Kind = FieldChangeKind.Rename, NewName = "Heading",
            State = FieldChangeState.Running, Total = 1, StartedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        var slug = TestNames.Slug("cg");
        var client = await _callers.SuperAdminAsync(tenant);
        var document = Document(slug, Home, [$"{type}.Ssn"]);

        var (_, plan) = await PlanAsync(client, document);
        var (applied, body) = await ApplyAsync(client, document);

        Changes(plan).Should().ContainSingle(c => c.StartsWith("connectors/" + slug));
        applied.IsSuccessStatusCode.Should().BeFalse(body.ToString());
        body.ToString().Should().Contain("field change running");
        (await _callers.InTenantAsync(tenant, s => s.Query<Connector>().AnyAsync(c => c.Slug == slug, Ct))).Should().BeFalse();
    }

    [Fact]
    public async Task A_plan_for_a_good_grant_creates_the_connector()
    {
        var (tenant, type) = await TenantWithTypeAsync();
        var slug = TestNames.Slug("cg");

        var (_, plan) = await PlanAsync(await _callers.SuperAdminAsync(tenant), Document(slug, Home, [$"{type}.Ssn"]));

        Changes(plan).Should().Equal($"connectors/{slug} create");
    }

    [Fact]
    public async Task A_config_apply_by_a_caller_who_may_not_read_the_field_is_refused_for_a_new_entry()
    {
        var (tenant, type) = await TenantWithTypeAsync();
        var slug = TestNames.Slug("cg");
        var manager = await HoldingAsync(tenant, SystemCapabilities.ManageConfig, SystemCapabilities.ManageConnectors, SystemCapabilities.ViewConnectors);

        var (applied, body) = await ApplyAsync(manager, Document(slug, Home, [$"{type}.Ssn"]));

        applied.IsSuccessStatusCode.Should().BeFalse(body.ToString());
        body.ToString().Should().Contain($"You may not read '{type}.Ssn'");
        (await _callers.InTenantAsync(tenant, s => s.Query<Connector>().AnyAsync(c => c.Slug == slug, Ct))).Should().BeFalse();
    }

    [Theory]
    [InlineData("https://crm.example.com/v2", true)]
    [InlineData("https://other.example.com/v1", true)]
    [InlineData("https://crm.example.com/v1/", false)]
    public async Task A_config_apply_that_moves_the_base_url_clears_the_grant_and_one_that_does_not_keeps_it(string next, bool clears)
    {
        var (tenant, type) = await TenantWithTypeAsync();
        var slug = TestNames.Slug("cg");
        await _callers.StoreInTenantAsync(tenant, Linked(slug, $"{type}.Ssn"));

        var (applied, body) = await ApplyAsync(await _callers.SuperAdminAsync(tenant), Document(slug, next, null));

        applied.StatusCode.Should().Be(HttpStatusCode.OK, body.ToString());
        var stored = await StoredAsync(tenant, slug);
        if (clears) stored.AllowedFields.Should().BeNull();
        else stored.AllowedFields.Should().Equal($"{type}.Ssn");
    }

    [Fact]
    public async Task A_document_with_no_list_keeps_the_stored_grant_and_an_empty_list_clears_it()
    {
        var (tenant, type) = await TenantWithTypeAsync();
        var slug = TestNames.Slug("cg");
        await _callers.StoreInTenantAsync(tenant, Linked(slug, $"{type}.Ssn"));
        var client = await _callers.SuperAdminAsync(tenant);

        (await ApplyAsync(client, Document(slug, Home, null))).Response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await StoredAsync(tenant, slug)).AllowedFields.Should().Equal($"{type}.Ssn");

        (await ApplyAsync(client, Document(slug, Home, []))).Response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await StoredAsync(tenant, slug)).AllowedFields.Should().BeNull();
    }

    [Fact]
    public async Task An_export_gives_the_grant_to_a_manager_and_not_to_a_caller_who_may_only_view_connectors()
    {
        var (tenant, type) = await TenantWithTypeAsync();
        var slug = TestNames.Slug("cg");
        await _callers.StoreInTenantAsync(tenant, Linked(slug, $"{type}.Ssn"));
        var viewer = await HoldingAsync(tenant, SystemCapabilities.ManageConfig, SystemCapabilities.ViewConnectors);

        var (_, managerExport) = await ExportAsync(await _callers.SuperAdminAsync(tenant), "connectors");
        var (viewerResponse, viewerExport) = await ExportAsync(viewer, "connectors");

        GrantsIn(managerExport.GetProperty("connectors").GetProperty(slug)).Should().Equal($"{type}.Ssn");
        viewerResponse.StatusCode.Should().Be(HttpStatusCode.OK, viewerExport.ToString());
        viewerExport.ToString().Should().NotContain($"{type}.Ssn");
        GrantsIn(viewerExport.GetProperty("connectors").GetProperty(slug)).Should().BeNull();
    }

    [Fact]
    public async Task A_plan_for_a_caller_who_may_only_view_connectors_shows_no_grant_and_says_so_in_one_note()
    {
        var (tenant, type) = await TenantWithTypeAsync();
        var slug = TestNames.Slug("cg");
        await _callers.StoreInTenantAsync(tenant, Linked(slug, $"{type}.Ssn"));
        var viewer = await HoldingAsync(tenant, SystemCapabilities.ManageConfig, SystemCapabilities.ViewConnectors);

        var (response, plan) = await PlanAsync(viewer, Document(slug, Home, []));

        response.StatusCode.Should().Be(HttpStatusCode.OK, plan.ToString());
        plan.ToString().Should().Contain("allowed fields are planned only for callers who may manage connectors");
        plan.ToString().Should().NotContain($"{type}.Ssn").And.NotContain("Ssn");
        Changes(plan).Should().BeEmpty("a grant does not count toward the status for a caller who may not see it");
    }

    [Fact]
    public async Task A_plan_for_a_caller_who_may_only_view_connectors_answers_alike_for_a_stored_and_an_unstored_entry()
    {
        var tenant = await _callers.TenantAsync();
        var type = TestNames.Compact("grp");
        await _callers.StoreInTenantAsync(tenant, TypeOf(
            type, Field("Title"), Field("Ssn", SensitivityLevel.Sensitive), Field("Tin", SensitivityLevel.Sensitive)));
        var slug = TestNames.Slug("cg");
        await _callers.StoreInTenantAsync(tenant, Linked(slug, $"{type}.Ssn"));
        var viewer = await HoldingAsync(tenant, SystemCapabilities.ManageConfig, SystemCapabilities.ViewConnectors);

        var (storedResponse, stored) = await PlanAsync(viewer, Document(slug, Home, [$"{type}.Ssn"]));
        var (unstoredResponse, unstored) = await PlanAsync(viewer, Document(slug, Home, [$"{type}.Tin"]));

        unstoredResponse.StatusCode.Should().Be(storedResponse.StatusCode, unstored.ToString());
        unstored.GetProperty("sections").ToString().Should().Be(stored.GetProperty("sections").ToString());
        unstored.ToString().Should().NotContain("Tin").And.NotContain("may not read");
        stored.ToString().Should().NotContain("Ssn");
    }

    [Fact]
    public async Task An_export_taken_without_the_grant_and_applied_back_keeps_it_and_the_plan_is_not_destructive()
    {
        var (tenant, type) = await TenantWithTypeAsync();
        var slug = TestNames.Slug("cg");
        await _callers.StoreInTenantAsync(tenant, Linked(slug, $"{type}.Ssn"));
        var viewer = await HoldingAsync(tenant, SystemCapabilities.ManageConfig, SystemCapabilities.ViewConnectors);
        var (_, export) = await ExportAsync(viewer, "connectors");
        var manager = await _callers.SuperAdminAsync(tenant);

        var (_, plan) = await PlanAsync(manager, export);
        var (applied, body) = await ApplyAsync(manager, export);

        plan.GetProperty("isEmpty").GetBoolean().Should().BeTrue(plan.ToString());
        Changes(plan).Should().BeEmpty();
        applied.StatusCode.Should().Be(HttpStatusCode.OK, body.ToString());
        (await StoredAsync(tenant, slug)).AllowedFields.Should().Equal($"{type}.Ssn");
    }

    [Fact]
    public async Task An_export_of_a_manager_applied_to_another_tenant_carries_the_grant_there_when_the_type_exists()
    {
        var (source, type) = await TenantWithTypeAsync();
        var target = await _callers.TenantAsync();
        await _callers.StoreInTenantAsync(target, TypeOf(type, Field("Title"), Field("Ssn", SensitivityLevel.Sensitive)));
        var slug = TestNames.Slug("cg");
        await _callers.StoreInTenantAsync(source, Linked(slug, $"{type}.Ssn"));
        var (_, export) = await ExportAsync(await _callers.SuperAdminAsync(source), "connectors");

        var (applied, body) = await ApplyAsync(await _callers.SuperAdminAsync(target), export);

        applied.StatusCode.Should().Be(HttpStatusCode.OK, body.ToString());
        (await StoredAsync(target, slug)).AllowedFields.Should().Equal($"{type}.Ssn");
    }

    private async Task<string> SeedFileAsync(object document)
    {
        var path = Path.Combine(Path.GetTempPath(), $"seed-grants-{Guid.NewGuid():n}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(document), Ct);
        await DataSeeder.SeedAsync(new HostShim(fixture.WithSettings(new Dictionary<string, string?>
        {
            ["Seed:File"] = path,
            ["Seed:DemoContent"] = "false",
        }).Services));
        return path;
    }

    private static object SeedOf(string tenant, object connectors) => new
    {
        version = 1,
        tenants = new Dictionary<string, object> { [tenant] = new { name = "Seeded Shop", config = new { connectors } } },
    };

    [Fact]
    public async Task A_seed_file_grants_a_field_without_a_user_and_audits_config_and_the_file_name()
    {
        var tenant = TestNames.Compact("seed-");
        var type = TestNames.Compact("grp");
        await _callers.StoreInTenantAsync(tenant, TypeOf(type, Field("Title"), Field("Ssn", SensitivityLevel.Sensitive)));
        var slug = TestNames.Slug("cg");
        var connectors = new Dictionary<string, object>
        {
            [slug] = new { name = "Link", baseUrl = Home, auth = "None", enabled = true, allowedFields = new[] { $"{type}.Ssn" } },
        };

        var path = await SeedFileAsync(SeedOf(tenant, connectors));

        (await StoredAsync(tenant, slug)).AllowedFields.Should().Equal($"{type}.Ssn");
        var row = (await AuditAsync(slug)).Should().ContainSingle().Subject;
        row.ActorUsername.Should().Be($"config:{Path.GetFileName(path)}");
        row.ActorUserId.Should().BeNull();
        row.Metadata!["allowedFieldsAfter"].ToString().Should().Be($"{type}.Ssn");
    }

    [Fact]
    public async Task A_seed_file_still_refuses_a_hidden_field_that_the_host_may_not_grant_either()
    {
        var tenant = TestNames.Compact("seed-");
        var type = TestNames.Compact("grp");
        await _callers.StoreInTenantAsync(tenant, TypeOf(type, Field("Title"), Field("Vault", SensitivityLevel.Hidden)));
        var slug = TestNames.Slug("cg");
        var connectors = new Dictionary<string, object>
        {
            [slug] = new { name = "Link", baseUrl = Home, auth = "None", enabled = true, allowedFields = new[] { $"{type}.Vault" } },
        };

        var act = () => SeedFileAsync(SeedOf(tenant, connectors));

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("Hidden");
        (await _callers.InTenantAsync(tenant, s => s.Query<Connector>().AnyAsync(c => c.Slug == slug, Ct))).Should().BeFalse();
    }

    [Fact]
    public async Task A_seed_that_skips_a_move_as_destructive_keeps_the_grant()
    {
        var tenant = TestNames.Compact("seed-");
        var type = TestNames.Compact("grp");
        await _callers.StoreInTenantAsync(tenant, TypeOf(type, Field("Title"), Field("Ssn", SensitivityLevel.Sensitive)));
        var slug = TestNames.Slug("cg");
        var held = Linked(slug, $"{type}.Ssn");
        held.Auth = ConnectorAuth.BearerToken;
        held.SecretKeys = ["Token"];
        await _callers.StoreInTenantAsync(tenant, held);
        var connectors = new Dictionary<string, object>
        {
            [slug] = new
            {
                name = "Link", baseUrl = "https://elsewhere.example.net", auth = "BearerToken", enabled = true,
                secrets = new[] { "Token" },
            },
        };

        await SeedFileAsync(SeedOf(tenant, connectors));

        var stored = await StoredAsync(tenant, slug);
        stored.BaseUrl.Should().Be(Home, "the move would clear a secret, so the seed skips it");
        stored.AllowedFields.Should().Equal(
            new[] { $"{type}.Ssn" }, "the move and the clear are never applied apart");
    }

    [Fact]
    public async Task The_same_connector_slug_and_type_name_in_two_tenants_are_judged_in_each_tenant()
    {
        var a = await _callers.TenantAsync();
        var b = await _callers.TenantAsync();
        var type = TestNames.Compact("grp");
        await _callers.StoreInTenantAsync(a, TypeOf(type, Field("Title"), Field("Ssn", SensitivityLevel.Sensitive)));
        await _callers.StoreInTenantAsync(b, TypeOf(type, Field("Title")));
        var inA = await _callers.SuperAdminAsync(a);
        var inB = await _callers.SuperAdminAsync(b);
        var body = new
        {
            name = "Link", slug = "shared-link", baseUrl = Home, auth = "None", settings = new { }, enabled = true,
            probePath = "/", allowedFields = new[] { $"{type}.Ssn" },
        };

        var inAResponse = await inA.PostAsJsonAsync("/api/connectors", body, Ct);
        var inBResponse = await inB.PostAsJsonAsync("/api/connectors", body, Ct);

        inAResponse.StatusCode.Should().Be(HttpStatusCode.OK, await inAResponse.Content.ReadAsStringAsync(Ct));
        inBResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await inBResponse.Content.ReadAsStringAsync(Ct)).Should().Contain("does not have");

        var plain = await inB.PostAsJsonAsync("/api/connectors", new
        {
            name = "Link", slug = "shared-link", baseUrl = Home, auth = "None", settings = new { }, enabled = true, probePath = "/",
        }, Ct);
        plain.StatusCode.Should().Be(HttpStatusCode.OK, await plain.Content.ReadAsStringAsync(Ct));
        GrantsIn(await (await inB.GetAsync("/api/connectors/shared-link", Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).Should().BeNull();
        GrantsIn(await (await inA.GetAsync("/api/connectors/shared-link", Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct))
            .Should().Equal($"{type}.Ssn");
    }

    [Fact]
    public async Task A_type_that_is_Hidden_in_one_tenant_is_refused_there_and_granted_where_it_is_Sensitive()
    {
        var a = await _callers.TenantAsync();
        var b = await _callers.TenantAsync();
        var type = TestNames.Compact("grp");
        await _callers.StoreInTenantAsync(a, TypeOf(type, Field("Ssn", SensitivityLevel.Sensitive)));
        await _callers.StoreInTenantAsync(b, TypeOf(type, Field("Ssn", SensitivityLevel.Hidden)));
        var body = new
        {
            name = "Link", slug = "level-link", baseUrl = Home, auth = "None", settings = new { }, enabled = true,
            probePath = "/", allowedFields = new[] { $"{type}.Ssn" },
        };

        var inA = await (await _callers.SuperAdminAsync(a)).PostAsJsonAsync("/api/connectors", body, Ct);
        var inB = await (await _callers.SuperAdminAsync(b)).PostAsJsonAsync("/api/connectors", body, Ct);

        inA.StatusCode.Should().Be(HttpStatusCode.OK, await inA.Content.ReadAsStringAsync(Ct));
        inB.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await inB.Content.ReadAsStringAsync(Ct)).Should().Contain("Hidden");
    }
}
