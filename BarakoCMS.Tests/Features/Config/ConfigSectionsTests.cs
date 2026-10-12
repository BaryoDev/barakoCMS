using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Models;
using static BarakoCMS.Tests.Features.Config.ConfigCallers;

namespace BarakoCMS.Tests.Features.Config;

/// <summary>
/// The workflow, permission rule, connector and site settings sections: the round trip #921 asks
/// for, secrets carried by name only, and what each one treats as destructive.
/// </summary>
[Collection("Sequential")]
public class ConfigSectionsTests
{
    private readonly ConfigCallers _callers;
    private readonly IntegrationTestFixture _fixture;

    public ConfigSectionsTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _callers = new ConfigCallers(fixture);
    }

    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():n}"[..(prefix.Length + 10)];

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.IsSuccessStatusCode.Should().BeTrue($"{(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement;
    }

    /// <summary>A tenant with a type, a role and its rule on the type, a workflow, a connector and a site entry.</summary>
    private async Task<(string Tenant, string Type, string Role, string Workflow, string Connector, string Secret)> SourceAsync()
    {
        var tenant = await _callers.TenantAsync();
        var type = Unique("visit");
        var role = Unique("Front Desk ");
        var workflow = Unique("notify-");
        var connector = Unique("crm-");
        var secret = "tok_" + Guid.NewGuid().ToString("N");

        var site = Type("site", ("Name", "string"), ("Tagline", "string"));
        site.IsSingleton = true;
        await _callers.StoreInTenantAsync(tenant, Type(type, ("Title", "string")), site);
        await _callers.StoreInTenantAsync(null, new Role
        {
            Id = Guid.NewGuid(), Name = role, SystemCapabilities = ["view_audit_log"],
            Permissions = [new ContentTypePermission { ContentTypeSlug = type, Read = new PermissionRule { Enabled = true } }],
        });

        var client = await _callers.SuperAdminAsync(tenant);
        await OkAsync(await client.PostAsJsonAsync("/api/workflows", new
        {
            name = workflow,
            triggerContentType = type,
            triggerEvent = "Created",
            actions = new[] { new { type = "Webhook", parameters = new Dictionary<string, string> { ["Url"] = "https://hooks.example.com/visit" } } },
        }, Ct));
        await OkAsync(await client.PostAsJsonAsync("/api/connectors", new
        {
            name = "CRM", slug = connector, baseUrl = "https://crm.example.com", auth = "BearerToken",
            secrets = new Dictionary<string, string> { ["Token"] = secret },
        }, Ct));
        await OkAsync(await client.PostAsJsonAsync("/api/contents", new
        {
            contentType = "site", status = "Published", data = new Dictionary<string, object> { ["Name"] = "Clinic", ["Tagline"] = "Open daily" },
        }, Ct));

        return (tenant, type, role, workflow, connector, secret);
    }

    [Fact]
    public async Task Export_then_apply_to_an_empty_tenant_reproduces_types_roles_workflows_and_site_settings()
    {
        var (source, type, role, workflow, connector, secret) = await SourceAsync();
        var target = await _callers.TenantAsync();

        var (exported, document) = await ExportAsync(await _callers.SuperAdminAsync(source));
        exported.StatusCode.Should().Be(HttpStatusCode.OK, document.ToString());
        document.ToString().Should().NotContain(secret, "an export never carries a secret's value");

        // Roles are stored once for the deployment, so the target would already hold this one. Gone,
        // the apply has to recreate it and its rule from the document, or nothing proves they travel.
        await using (var session = _fixture.Services.GetRequiredService<IDocumentStore>().LightweightSession())
        {
            session.DeleteWhere<Role>(r => r.Name == role);
            await session.SaveChangesAsync(Ct);
        }

        var targetClient = await _callers.SuperAdminAsync(target);
        var (applied, plan) = await ApplyAsync(targetClient, document);
        applied.StatusCode.Should().Be(HttpStatusCode.OK, plan.ToString());
        Changes(plan).Should().Contain(new[]
        {
            $"contentTypes/{type} create", "contentTypes/site create", $"workflows/{workflow} create",
            $"connectors/{connector} create", "siteSettings/site create",
            $"roles/{role} create", $"permissionRules/{role}/{type} create",
        });

        (await _callers.InTenantAsync(target, s => s.Query<ContentTypeDefinition>().AnyAsync(t => t.Name == type, Ct))).Should().BeTrue();
        var copied = await _callers.InTenantAsync(target, s => s.Query<WorkflowDefinition>().SingleAsync(w => w.Name == workflow, Ct));
        copied!.TriggerContentTypes.Should().Equal(type);
        copied.Actions.Single().Parameters["Url"].Should().Be("https://hooks.example.com/visit");
        var siteEntry = await _callers.InTenantAsync(target, s => s.Query<Content>().SingleAsync(c => c.ContentType == "site", Ct));
        siteEntry!.Data["Tagline"].Should().Be("Open daily");
        siteEntry.Status.Should().Be(ContentStatus.Published);
        var link = await _callers.InTenantAsync(target, s => s.Query<Connector>().SingleAsync(c => c.Slug == connector, Ct));
        link!.BaseUrl.Should().Be("https://crm.example.com");
        link.SecretKeys.Should().BeEmpty("a secret is carried by name, so the target still has to be given its value");
        var recreated = await _callers.InTenantAsync(null, s => s.Query<Role>().SingleAsync(r => r.Name == role, Ct));
        recreated!.SystemCapabilities.Should().Equal("view_audit_log");
        recreated.Permissions.Should().ContainSingle(p => p.ContentTypeSlug == type && p.Read.Enabled, "the rule came back with the role");

        var (_, second) = await PlanAsync(targetClient, document);
        second.GetProperty("isEmpty").GetBoolean().Should().BeTrue(second.ToString());
        second.ToString().Should().Contain("PUT /api/connectors/" + connector, "the missing secret is a note on every plan until it is set");
    }

    [Fact]
    public async Task A_connector_export_names_its_secret_and_never_carries_the_value()
    {
        var (tenant, _, _, _, connector, secret) = await SourceAsync();

        var (_, export) = await ExportAsync(await _callers.SuperAdminAsync(tenant), "connectors");

        var text = export.ToString();
        text.Should().Contain(connector);
        export.GetProperty("connectors").GetProperty(connector).GetProperty("secrets").EnumerateArray()
            .Select(s => s.GetString()).Should().Equal("Token");
        text.Should().NotContain(secret);
        text.Should().NotContain("protectedValue", "nor the stored ciphertext");
    }

    [Fact]
    public async Task A_document_carrying_a_secret_value_is_refused_and_stores_nothing()
    {
        var tenant = await _callers.TenantAsync();
        var slug = Unique("leak-");
        var client = await _callers.SuperAdminAsync(tenant);

        var (connectorResponse, connectorBody) = await ApplyAsync(client, new
        {
            version = 1,
            connectors = new Dictionary<string, object>
            {
                [slug] = new { name = "Leak", baseUrl = "https://leak.example.com", auth = "BearerToken", secrets = new Dictionary<string, string> { ["Token"] = "plain-value" } },
            },
        });
        connectorResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest, connectorBody.ToString());
        connectorBody.ToString().Should().Contain("never carries a secret's value");
        (await _callers.InTenantAsync(tenant, s => s.Query<Connector>().AnyAsync(c => c.Slug == slug, Ct))).Should().BeFalse();
        (await _callers.InTenantAsync(tenant, s => s.Query<ConnectorSecret>().AnyAsync(Ct))).Should().BeFalse();

        var (workflowResponse, workflowBody) = await ApplyAsync(client, new
        {
            version = 1,
            workflows = new Dictionary<string, object>
            {
                [Unique("hook-")] = new { triggerEvents = new[] { "Created" }, actions = new[] { new { type = "Webhook", parameters = new Dictionary<string, string> { ["Url"] = "https://h.example.com", ["Secret"] = "whsec-plain" } } } },
            },
        });
        workflowResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest, workflowBody.ToString());
        workflowBody.ToString().Should().Contain("carries a credential");
    }

    [Fact]
    public async Task A_workflow_change_keeps_the_stored_credential_and_the_export_leaves_it_out()
    {
        var tenant = await _callers.TenantAsync();
        var type = Unique("order");
        var name = Unique("signed-");
        var secret = "whsec_" + Guid.NewGuid().ToString("N");
        await _callers.StoreInTenantAsync(tenant, Type(type, ("Title", "string")));
        var client = await _callers.SuperAdminAsync(tenant);
        await OkAsync(await client.PostAsJsonAsync("/api/workflows", new
        {
            name, triggerContentType = type, triggerEvent = "Created",
            actions = new[] { new { type = "Webhook", parameters = new Dictionary<string, string> { ["Url"] = "https://hooks.example.com/o", ["Secret"] = secret } } },
        }, Ct));
        var before = (await _callers.InTenantAsync(tenant, s => s.Query<WorkflowDefinition>().SingleAsync(w => w.Name == name, Ct)))!.Actions.Single().Parameters["Secret"];

        var (_, export) = await ExportAsync(client, "workflows");
        export.ToString().Should().NotContain(secret).And.NotContain(before);
        export.GetProperty("gaps").GetProperty("workflows").ToString().Should().Contain("credential");

        var document = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(export.ToString())!;
        var workflows = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, JsonElement>>>(document["workflows"].ToString())!;
        workflows[name]["enabled"] = JsonSerializer.SerializeToElement(false);
        var (applied, plan) = await ApplyAsync(client, new { version = 1, workflows });

        applied.StatusCode.Should().Be(HttpStatusCode.OK, plan.ToString());
        Changes(plan).Should().Contain($"workflows/{name} change");
        var after = (await _callers.InTenantAsync(tenant, s => s.Query<WorkflowDefinition>().SingleAsync(w => w.Name == name, Ct)))!;
        after.Enabled.Should().BeFalse();
        after.Actions.Single().Parameters["Secret"].Should().Be(before, "the stored ciphertext is kept as it was, not dropped or encrypted again");
    }

    [Fact]
    public async Task A_workflow_left_out_is_deleted_only_with_the_flag()
    {
        var (tenant, _, _, workflow, _, _) = await SourceAsync();
        var client = await _callers.SuperAdminAsync(tenant);
        var document = new { version = 1, workflows = new Dictionary<string, object>() };

        var (_, plan) = await PlanAsync(client, document);
        Changes(plan).Should().Contain($"workflows/{workflow} destructive");

        (await ApplyAsync(client, document)).Response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _callers.InTenantAsync(tenant, s => s.Query<WorkflowDefinition>().AnyAsync(w => w.Name == workflow, Ct))).Should().BeTrue();

        (await ApplyAsync(client, document, allowDestructive: true)).Response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await _callers.InTenantAsync(tenant, s => s.Query<WorkflowDefinition>().AnyAsync(w => w.Name == workflow, Ct))).Should().BeFalse();
    }

    [Fact]
    public async Task Moving_a_connector_to_another_origin_clears_its_secrets_destructively()
    {
        var (tenant, _, _, _, connector, _) = await SourceAsync();
        var client = await _callers.SuperAdminAsync(tenant);
        var document = new
        {
            version = 1,
            connectors = new Dictionary<string, object>
            {
                [connector] = new { name = "CRM", baseUrl = "https://elsewhere.example.net", auth = "BearerToken", enabled = true, secrets = new[] { "Token" } },
            },
        };

        var (_, plan) = await PlanAsync(client, document);
        Changes(plan).Should().Equal($"connectors/{connector} destructive");
        plan.ToString().Should().Contain("secret Token is cleared");

        (await ApplyAsync(client, document)).Response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ApplyAsync(client, document, allowDestructive: true)).Response.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = await _callers.InTenantAsync(tenant, s => s.Query<Connector>().SingleAsync(c => c.Slug == connector, Ct));
        stored!.BaseUrl.Should().Be("https://elsewhere.example.net");
        stored.SecretKeys.Should().BeEmpty();
        (await _callers.InTenantAsync(tenant, s => s.Query<ConnectorSecret>().AnyAsync(x => x.ConnectorId == stored.Id, Ct)))
            .Should().BeFalse("the credential entered for the old address is gone, not sent to the new one");
    }

    [Fact]
    public async Task A_rule_left_out_is_removed_only_for_this_tenants_types()
    {
        var mine = await _callers.TenantAsync();
        var theirs = await _callers.TenantAsync();
        var myType = Unique("chart");
        var theirType = Unique("ledger");
        await _callers.StoreInTenantAsync(mine, Type(myType, ("Title", "string")));
        await _callers.StoreInTenantAsync(theirs, Type(theirType, ("Title", "string")));
        var role = Unique("Shared Clerk ");
        await _callers.StoreInTenantAsync(null, new Role
        {
            Id = Guid.NewGuid(), Name = role,
            Permissions =
            [
                new ContentTypePermission { ContentTypeSlug = myType, Read = new PermissionRule { Enabled = true } },
                new ContentTypePermission { ContentTypeSlug = theirType, Read = new PermissionRule { Enabled = true } },
            ],
        });
        var client = await _callers.SuperAdminAsync(mine);

        var (_, export) = await ExportAsync(client, "permissionRules");
        var keys = export.GetProperty("permissionRules").EnumerateObject().Select(p => p.Name).ToList();
        keys.Should().Contain($"{role}/{myType}").And.NotContain($"{role}/{theirType}", "another tenant's type is not this tenant's to export");

        var (applied, plan) = await ApplyAsync(client, new { version = 1, permissionRules = new Dictionary<string, object>() }, allowDestructive: true);
        applied.StatusCode.Should().Be(HttpStatusCode.OK, plan.ToString());
        Changes(plan).Should().Contain($"permissionRules/{role}/{myType} destructive");

        var stored = await _callers.InTenantAsync(null, s => s.Query<Role>().SingleAsync(r => r.Name == role, Ct));
        stored!.Permissions.Select(p => p.ContentTypeSlug).Should().Equal(new[] { theirType }, "the rule on the other tenant's type is left alone");
    }

    [Fact]
    public async Task A_cleared_site_field_is_destructive()
    {
        var (tenant, _, _, _, _, _) = await SourceAsync();
        var client = await _callers.SuperAdminAsync(tenant);
        var document = new
        {
            version = 1,
            siteSettings = new Dictionary<string, object> { ["site"] = new { status = "Published", data = new Dictionary<string, object> { ["Name"] = "Clinic" } } },
        };

        var (_, plan) = await PlanAsync(client, document);
        Changes(plan).Should().Equal("siteSettings/site destructive");
        plan.ToString().Should().Contain("'Tagline' is cleared");

        (await ApplyAsync(client, document)).Response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var site = await _callers.InTenantAsync(tenant, s => s.Query<Content>().SingleAsync(c => c.ContentType == "site", Ct));
        site!.Data.Should().ContainKey("Tagline", "a refused apply changes nothing");
    }

    /// <summary>A tenant holding one workflow whose webhooks each carry a secret, by URL.</summary>
    private async Task<(string Tenant, string Name, HttpClient Client)> SignedWorkflowAsync(params string[] urls)
    {
        var tenant = await _callers.TenantAsync();
        var type = Unique("order");
        var name = Unique("signed-");
        await _callers.StoreInTenantAsync(tenant, Type(type, ("Title", "string")));
        var client = await _callers.SuperAdminAsync(tenant);
        await OkAsync(await client.PostAsJsonAsync("/api/workflows", new
        {
            name, triggerContentType = type, triggerEvent = "Created",
            actions = urls.Select(u => new { type = "Webhook", parameters = new Dictionary<string, string> { ["Url"] = u, ["Secret"] = "whsec_" + Guid.NewGuid().ToString("N") } }).ToArray(),
        }, Ct));
        return (tenant, name, client);
    }

    private async Task<Dictionary<string, Dictionary<string, JsonElement>>> ExportedWorkflowsAsync(HttpClient client)
    {
        var (_, export) = await ExportAsync(client, "workflows");
        return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, JsonElement>>>(export.GetProperty("workflows").ToString())!;
    }

    private async Task<WorkflowDefinition> StoredWorkflowAsync(string tenant, string name) =>
        (await _callers.InTenantAsync(tenant, s => s.Query<WorkflowDefinition>().SingleAsync(w => w.Name == name, Ct)))!;

    [Fact]
    public async Task A_changed_webhook_url_drops_its_secret_destructively()
    {
        var (tenant, name, client) = await SignedWorkflowAsync("https://hooks.example.com/a");
        var secret = (await StoredWorkflowAsync(tenant, name)).Actions.Single().Parameters["Secret"];
        var workflows = await ExportedWorkflowsAsync(client);
        workflows[name]["actions"] = JsonSerializer.SerializeToElement(new[]
        {
            new { type = "Webhook", parameters = new Dictionary<string, string> { ["Url"] = "https://attacker.example.net/collect" } },
        });
        var document = new { version = 1, workflows };

        var (_, plan) = await PlanAsync(client, document);
        Changes(plan).Should().Equal($"workflows/{name} destructive");
        plan.ToString().Should().Contain("credential its stored copy held is dropped");

        (await ApplyAsync(client, document)).Response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await StoredWorkflowAsync(tenant, name)).Actions.Single().Parameters["Secret"].Should().Be(secret, "a refused apply changes nothing");

        (await ApplyAsync(client, document, allowDestructive: true)).Response.StatusCode.Should().Be(HttpStatusCode.OK);
        var action = (await StoredWorkflowAsync(tenant, name)).Actions.Single();
        action.Parameters["Url"].Should().Be("https://attacker.example.net/collect");
        action.Parameters.Should().NotContainKey("Secret", "a secret entered for one address is never sent to another");
    }

    [Fact]
    public async Task Reordering_two_webhooks_carries_neither_secret_to_the_other()
    {
        var (tenant, name, client) = await SignedWorkflowAsync("https://hooks.example.com/one", "https://hooks.example.com/two");
        var workflows = await ExportedWorkflowsAsync(client);
        var actions = workflows[name]["actions"].EnumerateArray().Select(a => a.Clone()).ToList();
        actions.Should().HaveCount(2);
        workflows[name]["actions"] = JsonSerializer.SerializeToElement(new[] { actions[1], actions[0] });

        var (applied, plan) = await ApplyAsync(client, new { version = 1, workflows }, allowDestructive: true);

        applied.StatusCode.Should().Be(HttpStatusCode.OK, plan.ToString());
        var stored = (await StoredWorkflowAsync(tenant, name)).Actions;
        stored.Select(a => a.Parameters["Url"]).Should().Equal("https://hooks.example.com/two", "https://hooks.example.com/one");
        stored.Should().OnlyContain(a => !a.Parameters.ContainsKey("Secret"), "neither secret follows its position onto the other address");
    }

    [Fact]
    public async Task Removing_a_rule_whose_type_another_tenant_also_has_says_so()
    {
        var mine = await _callers.TenantAsync();
        var theirs = await _callers.TenantAsync();
        var shared = Unique("shared");
        await _callers.StoreInTenantAsync(mine, Type(shared, ("Title", "string")));
        await _callers.StoreInTenantAsync(theirs, Type(shared, ("Title", "string")));
        var role = Unique("Both Clerk ");
        await _callers.StoreInTenantAsync(null, new Role
        {
            Id = Guid.NewGuid(), Name = role,
            Permissions = [new ContentTypePermission { ContentTypeSlug = shared, Read = new PermissionRule { Enabled = true } }],
        });

        var (_, plan) = await PlanAsync(await _callers.SuperAdminAsync(mine), new { version = 1, permissionRules = new Dictionary<string, object>() });

        Changes(plan).Should().Contain($"permissionRules/{role}/{shared} destructive");
        var notes = plan.GetProperty("sections")[0].GetProperty("notes").EnumerateArray().Select(n => n.GetString()!).ToList();
        notes.Should().ContainSingle(n => n.Contains($"'{role}/{shared}'") && n.Contains(theirs),
            "the rule lives on the shared role, so removing it here removes it for the other tenant too");
    }
}
