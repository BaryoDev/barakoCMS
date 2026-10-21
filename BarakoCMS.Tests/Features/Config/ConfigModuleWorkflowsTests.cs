using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Models;
using barakoCMS.Modules;
using static BarakoCMS.Tests.Features.Config.ConfigCallers;

namespace BarakoCMS.Tests.Features.Config;

/// <summary>
/// A workflow a module seeds is the module's, not the document's. Config apply leaves it alone,
/// refuses a document that rewrites it, and config export does not carry it.
/// </summary>
[Collection("Sequential")]
public class ConfigModuleWorkflowsTests
{
    private readonly ConfigCallers _callers;
    private readonly IntegrationTestFixture _fixture;

    public ConfigModuleWorkflowsTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _callers = new ConfigCallers(fixture);
    }

    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():n}"[..(prefix.Length + 10)];

    private static WorkflowDefinition Workflow(string name, string? owner) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        TriggerContentTypes = ["delivery"],
        TriggerEvents = ["Created"],
        TriggerContentType = "delivery",
        TriggerEvent = "Created",
        OwnerModule = owner,
        Actions = [new WorkflowAction { Type = "Webhook", Parameters = new() { ["Url"] = "https://hooks.example.com/a" } }],
    };

    private async Task<(string Tenant, HttpClient Client, string Owned, string Plain)> TenantWithBothAsync()
    {
        var tenant = await _callers.TenantAsync();
        var type = Unique("delivery");
        var owned = Unique("module-");
        var plain = Unique("config-");
        var delivery = new ContentTypeDefinition
        {
            Id = Guid.NewGuid(), Name = "delivery", DisplayName = "Delivery",
            Fields = [new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" }],
        };
        await _callers.StoreInTenantAsync(tenant, delivery, Workflow(owned, "Shipping"), Workflow(plain, null));
        return (tenant, await _callers.SuperAdminAsync(tenant), owned, plain);
    }

    [Fact]
    public async Task A_module_owned_workflow_left_out_of_the_document_is_not_planned_for_delete()
    {
        var (tenant, client, owned, plain) = await TenantWithBothAsync();
        var document = new { version = 1, workflows = new Dictionary<string, object>() };

        var (_, plan) = await PlanAsync(client, document);

        Changes(plan).Should().Contain($"workflows/{plain} destructive");
        Changes(plan).Should().NotContain(c => c.StartsWith($"workflows/{owned} "));

        (await ApplyAsync(client, document, allowDestructive: true)).Response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await _callers.InTenantAsync(tenant, s => s.Query<WorkflowDefinition>().AnyAsync(w => w.Name == owned, Ct))).Should().BeTrue();
        (await _callers.InTenantAsync(tenant, s => s.Query<WorkflowDefinition>().AnyAsync(w => w.Name == plain, Ct))).Should().BeFalse();
    }

    [Fact]
    public async Task A_normal_workflow_left_out_is_still_deleted_with_the_flag_and_refused_without()
    {
        var (tenant, client, _, plain) = await TenantWithBothAsync();
        var document = new { version = 1, workflows = new Dictionary<string, object>() };

        (await ApplyAsync(client, document)).Response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _callers.InTenantAsync(tenant, s => s.Query<WorkflowDefinition>().AnyAsync(w => w.Name == plain, Ct))).Should().BeTrue();
    }

    [Fact]
    public async Task A_document_naming_a_module_owned_workflow_with_other_content_is_refused()
    {
        var (tenant, client, owned, _) = await TenantWithBothAsync();
        var document = new
        {
            version = 1,
            workflows = new Dictionary<string, object>
            {
                [owned] = new
                {
                    triggerContentTypes = new[] { "delivery" },
                    triggerEvents = new[] { "Created" },
                    enabled = false,
                    actions = new[] { new { type = "Webhook", parameters = new Dictionary<string, string> { ["Url"] = "https://hooks.example.com/other" } } },
                },
            },
        };

        var (response, body) = await ApplyAsync(client, document, allowDestructive: true);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body.ToString());
        body.ToString().Should().Contain(owned).And.Contain("module");
        var stored = await _callers.InTenantAsync(tenant, s => s.Query<WorkflowDefinition>().SingleAsync(w => w.Name == owned, Ct));
        stored!.Enabled.Should().BeTrue();
        stored.Actions.Single().Parameters["Url"].Should().Be("https://hooks.example.com/a");
    }

    [Fact]
    public async Task The_export_leaves_a_module_owned_workflow_out_and_a_round_trip_applies_clean()
    {
        var (_, client, owned, plain) = await TenantWithBothAsync();

        var (_, export) = await ExportAsync(client, "workflows");
        var workflows = export.GetProperty("workflows");

        workflows.TryGetProperty(owned, out _).Should().BeFalse();
        workflows.TryGetProperty(plain, out _).Should().BeTrue();

        var (response, plan) = await ApplyAsync(client, new { version = 1, workflows });
        response.StatusCode.Should().Be(HttpStatusCode.OK, plan.ToString());
        Changes(plan).Should().BeEmpty();
    }

    [Fact]
    public async Task The_role_to_notify_on_failure_is_applied_exported_and_planned_when_it_changes()
    {
        var (tenant, client, _, _) = await TenantWithBothAsync();
        var name = Unique("notify-");
        object Document(string role) => new
        {
            version = 1,
            workflows = new Dictionary<string, object>
            {
                [name] = new
                {
                    triggerContentTypes = new[] { "delivery" },
                    triggerEvents = new[] { "Created" },
                    notifyRoleOnFailure = role,
                    actions = new[] { new { type = "Webhook", parameters = new Dictionary<string, string> { ["Url"] = "https://hooks.example.com/a" } } },
                },
            },
        };

        var (applied, body) = await ApplyAsync(client, Document("Ops"), allowDestructive: true);

        applied.StatusCode.Should().Be(HttpStatusCode.OK, body.ToString());
        var stored = await _callers.InTenantAsync(tenant, s => s.Query<WorkflowDefinition>().SingleAsync(w => w.Name == name, Ct));
        stored.NotifyRoleOnFailure.Should().Be("Ops");

        var (_, export) = await ExportAsync(client, "workflows");
        export.GetProperty("workflows").GetProperty(name).GetProperty("notifyRoleOnFailure").GetString().Should().Be("Ops");

        var (_, plan) = await PlanAsync(client, Document("Admins"));
        Changes(plan).Should().Contain(c => c.StartsWith($"workflows/{name} "));
    }

    [Fact]
    public async Task A_document_naming_a_module_owned_workflow_with_the_same_content_changes_nothing()
    {
        var (_, client, owned, _) = await TenantWithBothAsync();
        var document = new
        {
            version = 1,
            workflows = new Dictionary<string, object>
            {
                [owned] = new
                {
                    triggerContentTypes = new[] { "delivery" },
                    triggerEvents = new[] { "Created" },
                    enabled = true,
                    actions = new[] { new { type = "Webhook", parameters = new Dictionary<string, string> { ["Url"] = "https://hooks.example.com/a" } } },
                },
            },
        };

        var (response, plan) = await ApplyAsync(client, document, allowDestructive: true);

        response.StatusCode.Should().Be(HttpStatusCode.OK, plan.ToString());
        Changes(plan).Should().NotContain(c => c.StartsWith($"workflows/{owned} "));
    }

    [Fact]
    public async Task Seeding_stores_a_workflow_as_owned_and_marks_one_stored_before_owners_existed()
    {
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var fresh = Workflow(Unique("seed-fresh-"), null);
        var older = Workflow(Unique("seed-older-"), null);
        await using (var session = store.LightweightSession())
        {
            session.Store(older);
            await session.SaveChangesAsync(Ct);
        }

        await using (var session = store.LightweightSession())
        {
            await ModuleWorkflows.SeedAsync(session, "Shipping", [fresh, Workflow(older.Name, null).WithId(older.Id)], Ct);
            await session.SaveChangesAsync(Ct);
        }

        await using var read = store.QuerySession();
        (await read.LoadAsync<WorkflowDefinition>(fresh.Id, Ct))!.OwnerModule.Should().Be("Shipping");
        (await read.LoadAsync<WorkflowDefinition>(older.Id, Ct))!.OwnerModule.Should().Be("Shipping");
    }

    [Fact]
    public async Task Seeding_does_not_overwrite_a_workflow_that_is_already_stored()
    {
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var stored = Workflow(Unique("seed-kept-"), "Shipping");
        stored.Enabled = false;
        await using (var session = store.LightweightSession())
        {
            session.Store(stored);
            await session.SaveChangesAsync(Ct);
        }

        await using (var session = store.LightweightSession())
        {
            await ModuleWorkflows.SeedAsync(session, "Shipping", [Workflow(stored.Name, null).WithId(stored.Id)], Ct);
            await session.SaveChangesAsync(Ct);
        }

        await using var read = store.QuerySession();
        (await read.LoadAsync<WorkflowDefinition>(stored.Id, Ct))!.Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task A_module_owned_workflow_cannot_be_replaced_switched_or_deleted_over_the_api()
    {
        var (tenant, client, owned, _) = await TenantWithBothAsync();
        var id = (await _callers.InTenantAsync(tenant, s => s.Query<WorkflowDefinition>().FirstAsync(w => w.Name == owned, Ct))).Id;

        var put = await client.PutAsJsonAsync($"/api/workflows/{id}", new
        {
            name = owned,
            triggerContentTypes = new[] { "delivery" },
            triggerEvents = new[] { "Created" },
            actions = new[] { new { type = "Webhook", parameters = new Dictionary<string, string> { ["Url"] = "https://hooks.example.com/a" } } },
        }, Ct);
        var delete = await client.DeleteAsync($"/api/workflows/{id}", Ct);

        var switched = await client.PutAsJsonAsync($"/api/workflows/{id}/enabled", new { enabled = false }, Ct);

        put.StatusCode.Should().Be(HttpStatusCode.Conflict, await put.Content.ReadAsStringAsync(Ct));
        switched.StatusCode.Should().Be(HttpStatusCode.Conflict, await switched.Content.ReadAsStringAsync(Ct));
        (await _callers.InTenantAsync(tenant, s => s.LoadAsync<WorkflowDefinition>(id, Ct)))!.Enabled.Should().BeTrue();
        delete.StatusCode.Should().Be(HttpStatusCode.Conflict, await delete.Content.ReadAsStringAsync(Ct));
        (await _callers.InTenantAsync(tenant, s => s.LoadAsync<WorkflowDefinition>(id, Ct)))!.OwnerModule.Should().Be("Shipping");
    }
}

internal static class WorkflowTestExtensions
{
    public static WorkflowDefinition WithId(this WorkflowDefinition workflow, Guid id)
    {
        workflow.Id = id;
        return workflow;
    }
}
