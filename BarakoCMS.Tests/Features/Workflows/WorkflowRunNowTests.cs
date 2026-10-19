using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// POST /api/workflows/{id}/run starts one workflow on one entry, as a run with trigger Manual.
/// </summary>
/// <remarks>
/// Every refusal here has a request beside it that succeeds, so a route that refuses everything
/// cannot pass.
/// </remarks>
[Collection("Sequential")]
public class WorkflowRunNowTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly WorkflowStopHarness _harness;

    public WorkflowRunNowTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _harness = new WorkflowStopHarness(factory);
    }

    private static CancellationToken Ct => WorkflowStopHarness.Ct;

    private static string Url(Guid workflowId) => $"/api/workflows/{workflowId}/run";

    private static JsonContent Body(Guid entryId) => JsonContent.Create(new { entryId });

    private static HttpRequestMessage Post(Guid workflowId, Guid entryId, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Url(workflowId)) { Content = Body(entryId) };
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        return request;
    }

    /// <summary>A stored caller holding one role, so the run can name who started it.</summary>
    private async Task<(HttpClient Client, Guid UserId)> CallerAsync(Role role, string? tenantSlug = null, string? ip = null)
    {
        var userId = Guid.NewGuid();

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(role);
            session.Store(new User
            {
                Id = userId,
                Username = $"now-{userId:n}"[..16],
                Email = $"now-{userId:n}@example.com",
                RoleIds = [role.Id],
            });
            if (tenantSlug is not null)
            {
                session.Store(new Membership
                {
                    Id = Guid.NewGuid(), UserId = userId, TenantSlug = tenantSlug,
                    Status = MembershipStatus.Active, RoleIds = [role.Id],
                });
            }

            await session.SaveChangesAsync(Ct);
        }

        var claims = tenantSlug is null ? null : new Dictionary<string, string> { ["tenant"] = tenantSlug };
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: [role.Name], userId: userId.ToString(), additionalClaims: claims));
        if (tenantSlug is not null) client.DefaultRequestHeaders.Add("X-Tenant", tenantSlug);
        if (ip is not null) client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, ip);
        return (client, userId);
    }

    private Task<(HttpClient Client, Guid UserId)> AdminCallerAsync(string? tenantSlug = null, string? ip = null) =>
        CallerAsync(new Role
        {
            Id = Guid.NewGuid(),
            Name = $"Runner {Guid.NewGuid():N}",
            SystemCapabilities = [SystemCapabilities.RunWorkflows],
            Permissions = [new ContentTypePermission { ContentTypeSlug = "article", Read = new PermissionRule { Enabled = true } }],
        }, tenantSlug, ip);

    private async Task<Guid> WorkflowForArticlesAsync(Action<WorkflowDefinition>? shape = null) =>
        await _harness.StoreWorkflowAsync(w =>
        {
            w.TriggerContentType = "article";
            shape?.Invoke(w);
        });

    private async Task<string> TenantAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var slug = $"now-{Guid.NewGuid():N}"[..14].ToLowerInvariant();
        session.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug });
        await session.SaveChangesAsync(Ct);
        return slug;
    }

    private async Task<Guid> StoreInTenantAsync(string tenantSlug, WorkflowDefinition? workflow, bool withEntry)
    {
        var id = Guid.NewGuid();
        await using var session = _harness.Store.LightweightSession(tenantSlug);
        if (workflow is not null) session.Store(workflow);
        if (withEntry)
        {
            session.Store(new Content
            {
                Id = id, ContentType = "article", Status = ContentStatus.Published,
                Sensitivity = SensitivityLevel.Public, Data = new Dictionary<string, object>(),
            });
        }

        await session.SaveChangesAsync(Ct);
        return id;
    }

    private static WorkflowDefinition ArticleWorkflow() => new()
    {
        Id = Guid.NewGuid(),
        Name = WorkflowStopHarness.NewName("now"),
        TriggerContentType = "article",
        TriggerEvent = WorkflowEvents.Published,
        Actions = [new WorkflowAction { Type = WorkflowStopHarness.Counting }],
    };

    private static async Task<Guid> RunIdOfAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("runId").GetGuid();
    }

    [Fact]
    public async Task A_run_started_by_hand_reads_back_as_manual_and_names_who_started_it()
    {
        var (client, userId) = await AdminCallerAsync();
        var workflowId = await WorkflowForArticlesAsync();
        var entryId = await _harness.StoreContentAsync();

        var response = await client.PostAsync(Url(workflowId), Body(entryId), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var runId = await RunIdOfAsync(response);

        var run = await _harness.LoadRunAsync(runId);
        run.TriggerEvent.Should().Be(WorkflowEvents.Manual);
        run.StartedBy.Should().Be(userId);
        run.WorkflowDefinitionId.Should().Be(workflowId);
        run.ContentId.Should().Be(entryId);

        var read = await client.GetAsync($"/api/workflow-runs/{runId}", Ct);
        read.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await read.Content.ReadAsStringAsync(Ct));
        doc.RootElement.GetProperty("startedBy").GetGuid().Should().Be(userId);
        doc.RootElement.GetProperty("triggerEvent").GetString().Should().Be(WorkflowEvents.Manual);
    }

    [Fact]
    public async Task A_caller_without_run_workflows_gets_403_and_one_who_holds_it_gets_202()
    {
        var workflowId = await WorkflowForArticlesAsync();
        var entryId = await _harness.StoreContentAsync();

        var without = await _harness.WithoutTheCapabilityAsync();
        (await without.PostAsync(Url(workflowId), Body(entryId), Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var (holder, _) = await AdminCallerAsync();
        (await holder.PostAsync(Url(workflowId), Body(entryId), Ct)).StatusCode.Should().Be(HttpStatusCode.Accepted);

        (await _harness.RunsOfAsync(workflowId)).Should().HaveCount(1, "only the caller who held the capability started one");
    }

    [Fact]
    public async Task A_workflow_in_another_tenant_is_a_404_and_the_same_call_in_its_own_tenant_works()
    {
        var tenantA = await TenantAsync();
        var tenantB = await TenantAsync();
        var workflow = ArticleWorkflow();
        var entryInA = await StoreInTenantAsync(tenantA, workflow, withEntry: true);
        var entryInB = await StoreInTenantAsync(tenantB, null, withEntry: true);

        var (callerB, _) = await AdminCallerAsync(tenantB, "203.0.113.171");
        var (callerA, _) = await AdminCallerAsync(tenantA, "203.0.113.172");

        (await callerB.PostAsync(Url(workflow.Id), Body(entryInB), Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await callerA.PostAsync(Url(workflow.Id), Body(entryInA), Ct)).StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task An_entry_in_another_tenant_is_a_404_though_the_workflow_is_the_callers_own()
    {
        var tenantA = await TenantAsync();
        var tenantB = await TenantAsync();
        var workflow = ArticleWorkflow();
        await StoreInTenantAsync(tenantB, workflow, withEntry: false);
        var entryInA = await StoreInTenantAsync(tenantA, null, withEntry: true);
        var entryInB = await StoreInTenantAsync(tenantB, null, withEntry: true);

        var (callerB, _) = await AdminCallerAsync(tenantB, "203.0.113.173");

        (await callerB.PostAsync(Url(workflow.Id), Body(entryInA), Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await callerB.PostAsync(Url(workflow.Id), Body(entryInB), Ct)).StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task An_entry_the_caller_cannot_read_is_a_404_and_one_they_can_read_is_run()
    {
        var workflowId = await WorkflowForArticlesAsync();
        var entryId = await _harness.StoreContentAsync();

        var (blind, _) = await CallerAsync(new Role
        {
            Id = Guid.NewGuid(),
            Name = $"Blind runner {Guid.NewGuid():N}",
            SystemCapabilities = [SystemCapabilities.RunWorkflows],
        });
        var (reader, _) = await AdminCallerAsync();

        (await blind.PostAsync(Url(workflowId), Body(entryId), Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _harness.RunsOfAsync(workflowId)).Should().BeEmpty();

        (await reader.PostAsync(Url(workflowId), Body(entryId), Ct)).StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task An_entry_of_another_content_type_is_a_400()
    {
        var (client, _) = await AdminCallerAsync();
        var workflowId = await WorkflowForArticlesAsync(w => w.TriggerContentType = "invoice");
        var entryId = await _harness.StoreContentAsync();

        var response = await client.PostAsync(Url(workflowId), Body(entryId), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _harness.RunsOfAsync(workflowId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Conditions_that_do_not_match_are_a_409_that_names_the_field()
    {
        var (client, _) = await AdminCallerAsync();
        var workflowId = await WorkflowForArticlesAsync(w => w.Conditions = new() { ["Tier"] = "gold" });
        var silver = await _harness.StoreContentAsync(new() { ["Tier"] = "silver" });
        var gold = await _harness.StoreContentAsync(new() { ["Tier"] = "gold" });

        var refused = await client.PostAsync(Url(workflowId), Body(silver), Ct);

        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await refused.Content.ReadAsStringAsync(Ct)).Should().Contain("Tier");
        (await _harness.RunsOfAsync(workflowId)).Should().BeEmpty();

        (await client.PostAsync(Url(workflowId), Body(gold), Ct)).StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task A_disabled_workflow_is_a_409_and_a_definition_with_no_Enabled_key_counts_as_enabled()
    {
        var (client, _) = await AdminCallerAsync();
        var off = await WorkflowForArticlesAsync(w => w.Enabled = false);
        var legacy = await WorkflowForArticlesAsync();
        var entryId = await _harness.StoreContentAsync();

        await StripAsync("workflowdefinition", "Enabled", legacy);

        var refused = await client.PostAsync(Url(off), Body(entryId), Ct);

        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await refused.Content.ReadAsStringAsync(Ct)).Should().Contain("switched off");
        (await client.PostAsync(Url(legacy), Body(entryId), Ct)).StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Two_concurrent_posts_with_one_key_make_one_run_and_the_same_key_elsewhere_makes_another()
    {
        var (client, _) = await AdminCallerAsync();
        var workflowId = await WorkflowForArticlesAsync();
        var otherWorkflowId = await WorkflowForArticlesAsync();
        var entryId = await _harness.StoreContentAsync();
        var otherEntryId = await _harness.StoreContentAsync();
        var key = Guid.NewGuid().ToString("N");

        var both = await Task.WhenAll(
            client.SendAsync(Post(workflowId, entryId, key), Ct),
            client.SendAsync(Post(workflowId, entryId, key), Ct));

        both.Select(r => r.StatusCode).Should().OnlyContain(s => s == HttpStatusCode.Accepted || s == HttpStatusCode.OK);
        var ids = await Task.WhenAll(both.Select(RunIdOfAsync));
        ids.Distinct().Should().HaveCount(1);
        (await _harness.RunsOfAsync(workflowId)).Should().HaveCount(1);

        var otherWorkflow = await client.SendAsync(Post(otherWorkflowId, entryId, key), Ct);
        var otherEntry = await client.SendAsync(Post(workflowId, otherEntryId, key), Ct);

        otherWorkflow.StatusCode.Should().Be(HttpStatusCode.Accepted);
        otherEntry.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await RunIdOfAsync(otherWorkflow)).Should().NotBe(ids[0]);
        (await RunIdOfAsync(otherEntry)).Should().NotBe(ids[0]);
        (await _harness.RunsOfAsync(workflowId)).Should().HaveCount(2);
        (await _harness.RunsOfAsync(otherWorkflowId)).Should().HaveCount(1);
    }

    [Fact]
    public async Task A_manual_run_is_not_skipped_as_overtaken_and_runs_to_completion()
    {
        var (client, _) = await AdminCallerAsync();
        var workflowId = await WorkflowForArticlesAsync();
        var entryId = await _harness.StoreContentAsync();
        Guid runId = default;

        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            var response = await client.PostAsync(Url(workflowId), Body(entryId), Ct);
            response.StatusCode.Should().Be(HttpStatusCode.Accepted);
            runId = await RunIdOfAsync(response);
            await _harness.DrainAsync();
        });

        var run = await _harness.LoadRunAsync(runId);
        run.TriggeringEventSequence.Should().Be(0);
        run.Status.Should().Be(RunStatus.Succeeded);
        run.Actions.Should().ContainSingle();
        run.Actions[0].Status.Should().Be(AttemptStatus.Succeeded);
        WorkflowStopHarness.TimesRun(run.Actions[0]).Should().Be(1);
    }

    [Fact]
    public async Task The_audit_log_records_the_manual_run_with_the_actor()
    {
        var (client, userId) = await AdminCallerAsync();
        var workflowId = await WorkflowForArticlesAsync();
        var entryId = await _harness.StoreContentAsync();

        var response = await client.PostAsync(Url(workflowId), Body(entryId), Ct);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var runId = await RunIdOfAsync(response);

        var rows = await _harness.AuditOfAsync("workflow.run.manual", workflowId);

        rows.Should().HaveCount(1);
        rows[0].ActorUserId.Should().Be(userId);
        rows[0].Metadata.Should().ContainKey("runId");
        rows[0].Metadata!["runId"].ToString().Should().Contain(runId.ToString());
    }

    [Fact]
    public async Task A_run_document_stored_before_StartedBy_existed_still_loads()
    {
        var run = await _harness.SeedRunAsync(Guid.NewGuid(), Guid.NewGuid(), [WorkflowStopHarness.Waiting()]);

        await StripAsync("workflow_runs", "StartedBy", run.Id);

        var loaded = await _harness.LoadRunAsync(run.Id);

        loaded.StartedBy.Should().BeNull();
        loaded.TriggerEvent.Should().Be(WorkflowEvents.Published);
    }

    [Fact]
    public async Task The_health_route_is_not_taken_as_a_workflow_id()
    {
        var client = await _harness.AdminAsync();

        var response = await client.GetAsync("/api/workflows/health", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        doc.RootElement.TryGetProperty("windowDays", out _).Should().BeTrue("the health response, not a workflow, answered");
    }

    [Fact]
    public void A_role_that_manages_workflows_is_granted_run_workflows_and_one_that_does_not_is_left_alone()
    {
        var manager = new Role { Name = "m", SystemCapabilities = [SystemCapabilities.ManageWorkflows] };
        var other = new Role { Name = "o", SystemCapabilities = [SystemCapabilities.ViewWorkflowRuns] };

        barakoCMS.Data.DataSeeder.GrantRunWorkflows(manager).Should().BeTrue();
        barakoCMS.Data.DataSeeder.GrantRunWorkflows(manager).Should().BeFalse("it already holds it");
        barakoCMS.Data.DataSeeder.GrantRunWorkflows(other).Should().BeFalse();

        manager.SystemCapabilities.Should().Contain(SystemCapabilities.RunWorkflows);
        other.SystemCapabilities.Should().NotContain(SystemCapabilities.RunWorkflows);
    }

    [Fact]
    public async Task A_grant_an_administrator_removed_stays_removed_when_the_seeder_runs_again()
    {
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"Flow author {Guid.NewGuid():N}",
            SystemCapabilities = [SystemCapabilities.ManageWorkflows],
        };

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(role);
            await session.SaveChangesAsync(Ct);
            await barakoCMS.Data.DataSeeder.SeedRolesAsync(session);
        }

        var stored = await LoadRoleAsync(role.Id);
        stored.SystemCapabilities.Should().Contain(SystemCapabilities.ManageWorkflows);
        stored.SystemCapabilities.Should().NotContain(SystemCapabilities.RunWorkflows,
            "the one-time grant already ran at startup, before this role existed");

        // The first run of the grant on a database that has not had it: no marker.
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Delete<SeedMarker>(barakoCMS.Data.DataSeeder.RunWorkflowsGrantKey);
            await session.SaveChangesAsync(Ct);
            await barakoCMS.Data.DataSeeder.SeedRolesAsync(session);
        }

        (await LoadRoleAsync(role.Id)).SystemCapabilities.Should().Contain(SystemCapabilities.RunWorkflows);

        // The administrator takes it away, and the seeder runs again.
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var again = await session.LoadAsync<Role>(role.Id, Ct);
            again!.SystemCapabilities = [SystemCapabilities.ManageWorkflows];
            session.Store(again);
            await session.SaveChangesAsync(Ct);
            await barakoCMS.Data.DataSeeder.SeedRolesAsync(session);
        }

        (await LoadRoleAsync(role.Id)).SystemCapabilities.Should().NotContain(SystemCapabilities.RunWorkflows);
    }

    private async Task<Role> LoadRoleAsync(Guid id)
    {
        await using var session = _harness.Store.QuerySession();
        var role = await session.LoadAsync<Role>(id, Ct);
        role.Should().NotBeNull();
        return role!;
    }

    private async Task StripAsync(string table, string field, Guid id)
    {
        await using var session = _harness.Store.LightweightSession();
        session.QueueSqlCommand(
            $"update {_harness.Store.Options.DatabaseSchemaName}.mt_doc_{table} set data = data - '{field}' where id = ?",
            id);
        await session.SaveChangesAsync(Ct);
    }
}
