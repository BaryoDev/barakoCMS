using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Infrastructure.Security;
using barakoCMS.Models;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// <c>PUT /api/workflows/{id}</c> replaces a workflow, validated the way a create is (#677).
/// </summary>
[Collection("Sequential")]
public class WorkflowUpdateTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly WorkflowStopHarness _harness;

    public WorkflowUpdateTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _harness = new WorkflowStopHarness(factory);
    }

    private static CancellationToken Ct => WorkflowStopHarness.Ct;

    private string Unprotect(string value) =>
        _factory.Services.GetRequiredService<ISecretProtector>().Unprotect(value)!;

    private static object Email(string to) =>
        new { type = "Email", parameters = new Dictionary<string, string> { ["To"] = to, ["Subject"] = "s", ["Body"] = "b" } };

    private static object Webhook(string url, string? secret = null)
    {
        var parameters = new Dictionary<string, string> { ["Url"] = url };
        if (secret is not null)
        {
            parameters["Secret"] = secret;
        }

        return new { type = "Webhook", parameters };
    }

    private static object Body(string name, string contentType, string triggerEvent, params object[] actions) =>
        new { name, triggerContentType = contentType, triggerEvent, actions };

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private async Task<Guid> CreateAsync(HttpClient admin, params object[] actions)
    {
        var res = await admin.PostAsJsonAsync("/api/workflows",
            Body(WorkflowStopHarness.NewName("wf-put"), WorkflowStopHarness.NewName("post"), "Published", actions), Ct);
        var text = await res.Content.ReadAsStringAsync(Ct);
        res.IsSuccessStatusCode.Should().BeTrue(text);
        return JsonDocument.Parse(text).RootElement.GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task A_valid_definition_replaces_the_stored_one_and_is_audited()
    {
        var admin = await _harness.AdminAsync();
        var id = await CreateAsync(admin, Email("before@example.com"));
        var name = WorkflowStopHarness.NewName("renamed");
        var contentType = WorkflowStopHarness.NewName("page");

        var response = await OkAsync(await admin.PutAsJsonAsync($"/api/workflows/{id}",
            new { name, triggerContentType = contentType, triggerEvent = "Created", enabled = false, actions = new[] { Email("after@example.com") } }, Ct));

        response.GetProperty("id").GetGuid().Should().Be(id);
        response.GetProperty("name").GetString().Should().Be(name);
        response.GetProperty("enabled").GetBoolean().Should().BeFalse();
        response.GetProperty("warnings").GetArrayLength().Should().Be(0);

        var stored = await _harness.LoadWorkflowAsync(id);
        stored!.Name.Should().Be(name);
        stored.TriggerContentType.Should().Be(contentType);
        stored.TriggerEvent.Should().Be("Created");
        stored.Enabled.Should().BeFalse();
        stored.Actions.Should().ContainSingle().Which.Parameters["To"].Should().Be("after@example.com");

        (await _harness.AuditOfAsync("workflow.updated", id)).Should().HaveCount(1);
    }

    [Fact]
    public async Task An_invalid_definition_is_refused_and_the_stored_one_is_unchanged()
    {
        var admin = await _harness.AdminAsync();
        var id = await CreateAsync(admin, Email("kept@example.com"));
        var before = await _harness.LoadWorkflowAsync(id);

        var response = await admin.PutAsJsonAsync($"/api/workflows/{id}",
            Body("broken", "post", "Published", new { type = "NoSuchAction", parameters = new Dictionary<string, string>() }), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var after = await _harness.LoadWorkflowAsync(id);
        after!.Name.Should().Be(before!.Name);
        after.Actions.Should().ContainSingle().Which.Parameters["To"].Should().Be("kept@example.com");
        (await _harness.AuditOfAsync("workflow.updated", id)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_workflow_that_does_not_exist_is_not_found_and_not_created()
    {
        var admin = await _harness.AdminAsync();
        var id = Guid.NewGuid();

        var response = await admin.PutAsJsonAsync($"/api/workflows/{id}",
            Body("ghost", "post", "Published", Email("a@example.com")), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _harness.LoadWorkflowAsync(id)).Should().BeNull();
    }

    [Fact]
    public async Task Updating_needs_a_token_and_the_capability()
    {
        var admin = await _harness.AdminAsync();
        var id = await CreateAsync(admin, Email("kept@example.com"));
        var body = Body("changed", "post", "Published", Email("changed@example.com"));

        (await _harness.Anonymous().PutAsJsonAsync($"/api/workflows/{id}", body, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await (await _harness.WithoutTheCapabilityAsync()).PutAsJsonAsync($"/api/workflows/{id}", body, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _harness.LoadWorkflowAsync(id))!.Actions.Single().Parameters["To"].Should().Be("kept@example.com");

        (await admin.PutAsJsonAsync($"/api/workflows/{id}", body, Ct)).StatusCode.Should().Be(HttpStatusCode.OK,
            "the same request from a caller who holds the capability is served, so the refusals are the gate");
    }

    [Fact]
    public async Task An_unchanged_action_keeps_its_secret_a_blank_one_clears_it_and_a_new_one_replaces_it()
    {
        var admin = await _harness.AdminAsync();
        var id = await CreateAsync(admin, Webhook("https://hooks.example.com/a", "first-secret"));
        var created = (await _harness.LoadWorkflowAsync(id))!.Actions.Single().Parameters["Secret"];
        Unprotect(created).Should().Be("first-secret");

        var kept = await OkAsync(await admin.PutAsJsonAsync($"/api/workflows/{id}",
            Body("renamed", "post", "Published", Webhook("https://hooks.example.com/a")), Ct));
        kept.GetProperty("actions")[0].GetProperty("secretSet").GetBoolean().Should().BeTrue();
        kept.GetRawText().Should().NotContain("first-secret").And.NotContain(created);
        (await _harness.LoadWorkflowAsync(id))!.Actions.Single().Parameters["Secret"]
            .Should().Be(created, "the stored ciphertext is kept as it was");

        await OkAsync(await admin.PutAsJsonAsync($"/api/workflows/{id}",
            Body("replaced", "post", "Published", Webhook("https://hooks.example.com/b", "second-secret")), Ct));
        var replaced = (await _harness.LoadWorkflowAsync(id))!.Actions.Single();
        replaced.Parameters["Url"].Should().Be("https://hooks.example.com/b");
        Unprotect(replaced.Parameters["Secret"]).Should().Be("second-secret");

        var cleared = await OkAsync(await admin.PutAsJsonAsync($"/api/workflows/{id}",
            Body("cleared", "post", "Published", Webhook("https://hooks.example.com/b", "")), Ct));
        cleared.GetProperty("actions")[0].GetProperty("secretSet").GetBoolean().Should().BeFalse();
        (await _harness.LoadWorkflowAsync(id))!.Actions.Single().Parameters.Should().NotContainKey("Secret");
    }

    [Fact]
    public async Task A_secret_is_not_kept_for_an_action_pointed_somewhere_else()
    {
        var admin = await _harness.AdminAsync();
        var id = await CreateAsync(admin, Webhook("https://hooks.example.com/a", "first-secret"));
        var created = (await _harness.LoadWorkflowAsync(id))!.Actions.Single().Parameters["Secret"];

        var response = await admin.PutAsJsonAsync($"/api/workflows/{id}",
            Body("moved", "post", "Published", Webhook("https://attacker.example.net/collect")), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("Secret");
        var stored = (await _harness.LoadWorkflowAsync(id))!.Actions.Single();
        stored.Parameters["Url"].Should().Be("https://hooks.example.com/a", "nothing was saved");
        stored.Parameters["Secret"].Should().Be(created);
    }

    [Fact]
    public async Task Two_webhooks_swapped_do_not_take_each_others_secrets()
    {
        var admin = await _harness.AdminAsync();
        var id = await CreateAsync(admin,
            Webhook("https://hooks.example.com/a", "secret-a"),
            Webhook("https://hooks.example.com/b", "secret-b"));

        var response = await admin.PutAsJsonAsync($"/api/workflows/{id}",
            Body("swapped", "post", "Published",
                Webhook("https://hooks.example.com/b"), Webhook("https://hooks.example.com/a")), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var actions = (await _harness.LoadWorkflowAsync(id))!.Actions;
        actions.Should().HaveCount(2);
        Unprotect(actions[0].Parameters["Secret"]).Should().Be("secret-a");
        Unprotect(actions[1].Parameters["Secret"]).Should().Be("secret-b");
    }

    [Fact]
    public async Task A_secret_is_not_carried_to_an_action_of_another_type_in_its_place()
    {
        var admin = await _harness.AdminAsync();
        var id = await CreateAsync(admin, Webhook("https://hooks.example.com/a", "first-secret"), Email("x@example.com"));

        await OkAsync(await admin.PutAsJsonAsync($"/api/workflows/{id}",
            Body("swapped", "post", "Published", Email("x@example.com"), Webhook("https://hooks.example.com/a", "new-secret")), Ct));

        var actions = (await _harness.LoadWorkflowAsync(id))!.Actions;
        actions.Should().HaveCount(2);
        actions[0].Parameters.Should().NotContainKey("Secret");
        Unprotect(actions[1].Parameters["Secret"]).Should().Be("new-secret", "the one sent, not one moved over");
    }

    [Fact]
    public async Task A_conditional_sent_back_as_it_was_read_keeps_its_childs_secret()
    {
        var admin = await _harness.AdminAsync();
        var branch = JsonSerializer.Serialize(new[]
        {
            new { Type = "Webhook", Parameters = new Dictionary<string, string> { ["Url"] = "https://hooks.example.com/child", ["Secret"] = "child-secret" } },
        });
        var conditional = new
        {
            type = "Conditional",
            parameters = new Dictionary<string, string> { ["Condition"] = "{{status}} == Published", ["ThenActions"] = branch },
        };

        var created = await admin.PostAsJsonAsync("/api/workflows",
            Body(WorkflowStopHarness.NewName("wf-put"), WorkflowStopHarness.NewName("post"), "Published", conditional), Ct);
        var createdText = await created.Content.ReadAsStringAsync(Ct);
        created.IsSuccessStatusCode.Should().BeTrue(createdText);
        using var createdDoc = JsonDocument.Parse(createdText);
        var id = createdDoc.RootElement.GetProperty("id").GetGuid();
        var shownParameters = createdDoc.RootElement.GetProperty("actions")[0].GetProperty("parameters")
            .Deserialize<Dictionary<string, string>>()!;
        shownParameters["ThenActions"].Should().NotContain("child-secret");
        var storedBranch = (await _harness.LoadWorkflowAsync(id))!.Actions.Single().Parameters["ThenActions"];

        var updated = await OkAsync(await admin.PutAsJsonAsync($"/api/workflows/{id}",
            Body("renamed", "post", "Published", new { type = "Conditional", parameters = shownParameters }), Ct));

        updated.GetProperty("name").GetString().Should().Be("renamed");
        (await _harness.LoadWorkflowAsync(id))!.Actions.Single().Parameters["ThenActions"]
            .Should().Be(storedBranch, "the branch came back as it was shown, so the stored one, child secret and all, is kept");
    }
}
