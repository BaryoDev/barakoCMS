using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Features.Workflows;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// The Unpublished and Deleted triggers, and a workflow that names several events.
/// </summary>
/// <remarks>
/// Every count of zero sits beside a count of one from the same test, so a queue that fires nothing
/// cannot pass. The Unpublished test goes through the daemon and polls; the rest call the queue or
/// the erase endpoint, which write their runs before they return.
/// </remarks>
[Collection("Sequential")]
public class WorkflowTriggerEventsTests
{
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(30);

    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;

    public WorkflowTriggerEventsTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Taking_a_published_entry_down_fires_the_Unpublished_workflow_once()
    {
        await AuthenticateAsAdminAsync();
        var contentType = NewName("wf");
        var workflowId = await StoreWorkflowAsync(w =>
        {
            w.TriggerContentType = contentType;
            w.TriggerEvent = WorkflowEvents.Unpublished;
        });

        // Archived without ever being Published. Created first, so the daemon has passed it by the
        // time the run for the second entry exists.
        var neverPublished = await CreateContentAsync(contentType);
        (await ChangeStatusAsync(neverPublished, ContentStatus.Archived)).EnsureSuccessStatusCode();

        var takenDown = await CreateContentAsync(contentType);
        (await ChangeStatusAsync(takenDown, ContentStatus.Published)).EnsureSuccessStatusCode();
        (await ChangeStatusAsync(takenDown, ContentStatus.Archived)).EnsureSuccessStatusCode();

        await PollAsync(
            async () => (await RunsOfAsync(workflowId)).Count > 0,
            "archiving a Published entry queued no Unpublished run");

        var runs = await RunsOfAsync(workflowId);
        runs.Should().HaveCount(1, "one entry left Published, once, and the other was never Published");
        runs[0].ContentId.Should().Be(takenDown);
        runs[0].TriggerEvent.Should().Be(WorkflowEvents.Unpublished);
    }

    [Fact]
    public async Task Erasing_an_entry_fires_the_Deleted_workflow_once_with_the_id_and_type_only()
    {
        await AuthenticateAsync("SuperAdmin");
        var contentType = NewName("wf");
        var needle = $"erased-{Guid.NewGuid():n}";

        var workflowId = await StoreWorkflowAsync(w =>
        {
            w.TriggerContentType = contentType;
            w.TriggerEvent = WorkflowEvents.Deleted;
        });
        var conditionalId = await StoreWorkflowAsync(w =>
        {
            w.TriggerContentType = contentType;
            w.TriggerEvent = WorkflowEvents.Deleted;
            w.Conditions = new Dictionary<string, string> { ["FullName"] = needle };
        });

        var id = await SeedAsync(contentType, needle);

        (await _client.DeleteAsync($"/api/contents/{id}/erase")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var runs = await RunsOfAsync(workflowId);
        runs.Should().HaveCount(1);
        runs[0].ContentId.Should().Be(id);
        runs[0].ContentType.Should().Be(contentType);
        runs[0].TriggerEvent.Should().Be(WorkflowEvents.Deleted);
        JsonSerializer.Serialize(runs[0]).Should().NotContain(needle,
            "a run that quotes what was erased puts it back");

        (await RunsOfAsync(conditionalId)).Count.Should().Be(0,
            "its condition reads the entry's data, and the Deleted trigger is given none");
    }

    [Fact]
    public async Task The_action_of_a_Deleted_workflow_runs_and_is_handed_the_id_and_type_only()
    {
        await AuthenticateAsync("SuperAdmin");
        var contentType = NewName("wf");
        var needle = $"erased-{Guid.NewGuid():n}";

        // The template names the erased field. With the entry's data it would resolve to the needle.
        var workflowId = await StoreWorkflowAsync(w =>
        {
            w.TriggerContentType = contentType;
            w.TriggerEvent = WorkflowEvents.Deleted;
            w.Actions =
            [
                new WorkflowAction
                {
                    Type = "DeletedEcho",
                    Parameters = new Dictionary<string, string>
                    {
                        ["Note"] = "{{data.FullName}}",
                        ["When"] = "{{status}}|{{createdAt}}|{{updatedAt}}",
                    },
                },
            ];
        });

        var id = await SeedAsync(contentType, needle);
        (await _client.DeleteAsync($"/api/contents/{id}/erase")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        await DrainAsync(workflowId);

        var runs = await RunsOfAsync(workflowId);
        runs.Should().HaveCount(1);
        runs[0].Actions.Should().HaveCount(1);
        runs[0].Actions[0].Status.Should().Be(AttemptStatus.Succeeded,
            "the entry is always gone for a Deleted run, so skipping on a missing entry means the action never runs: {0}",
            runs[0].Actions[0].Error);

        DeletedEchoAction.ReceivedByRun.TryGetValue(runs[0].Id.ToString(), out var received).Should().BeTrue(
            "the action has to have been called for this run");
        received!.ContentId.Should().Be(id);
        received.ContentType.Should().Be(contentType);
        received.ContentJson.Should().NotContain(needle, "the action is told which entry went, not what it held");
        received.ParametersJson.Should().NotContain(needle, "a template cannot read data the run was never given");
        received.ParametersJson.Should().Contain("\"TriggerEvent\":\"Deleted\"",
            "the trigger is how a custom action learns the entry was erased");
        received.ParametersJson.Should().Contain("\"When\":\"||\"",
            "an erased entry has no status or timestamps, and Draft and the time of the run would be invented");
    }

    [Fact]
    public async Task Erasing_an_entry_created_again_under_the_same_id_fires_Deleted_again()
    {
        await AuthenticateAsync("SuperAdmin");
        var contentType = NewName("wf");
        var workflowId = await StoreWorkflowAsync(w =>
        {
            w.TriggerContentType = contentType;
            w.TriggerEvent = WorkflowEvents.Deleted;
        });

        var id = Guid.NewGuid();
        await SeedAsync(contentType, "first", id);
        (await _client.DeleteAsync($"/api/contents/{id}/erase")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await RunsOfAsync(workflowId)).Should().HaveCount(1);

        await SeedAsync(contentType, "second", id);
        (await _client.DeleteAsync($"/api/contents/{id}/erase")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await RunsOfAsync(workflowId)).Should().HaveCount(2, "each erasure is its own event, whatever the id");
    }

    [Fact]
    public async Task A_conditional_on_a_Deleted_run_refuses_a_condition_on_status_and_runs_one_on_the_content_type()
    {
        await AuthenticateAsync("SuperAdmin");
        var contentType = NewName("wf");
        var refusedChild = NewName("child");
        var allowedChild = NewName("child");

        var refusedId = await StoreWorkflowAsync(w =>
        {
            w.TriggerContentType = contentType;
            w.TriggerEvent = WorkflowEvents.Deleted;
            w.Actions = [Conditional("{{status}} == Draft", refusedChild)];
        });
        var allowedId = await StoreWorkflowAsync(w =>
        {
            w.TriggerContentType = contentType;
            w.TriggerEvent = WorkflowEvents.Deleted;
            w.Actions = [Conditional($"{{{{contentType}}}} == {contentType}", allowedChild)];
        });

        var id = await SeedAsync(contentType, $"erased-{Guid.NewGuid():n}");
        (await _client.DeleteAsync($"/api/contents/{id}/erase")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        await DrainAsync(refusedId, allowedId);

        var allowed = await RunsOfAsync(allowedId);
        allowed.Should().HaveCount(1);
        allowed[0].Actions.Should().HaveCount(1);
        allowed[0].Actions[0].Status.Should().Be(AttemptStatus.Succeeded, "{0}", allowed[0].Actions[0].Error);
        DeletedEchoAction.ReceivedByRun.TryGetValue(allowedChild, out var ran).Should().BeTrue(
            "the content type is known for an erased entry, so the branch runs");
        ran!.ContentId.Should().Be(id);

        var refused = await RunsOfAsync(refusedId);
        refused.Should().HaveCount(1);
        refused[0].Actions.Should().HaveCount(1);
        refused[0].Actions[0].Status.Should().Be(AttemptStatus.Failed);
        refused[0].Actions[0].Retryable.Should().BeFalse("the entry will not come back on a retry");
        refused[0].Actions[0].Error.Should().Contain("erased");
        DeletedEchoAction.ReceivedByRun.ContainsKey(refusedChild).Should().BeFalse(
            "a branch chosen on a status nobody stored must not run");
    }

    [Fact]
    public async Task A_workflow_naming_several_types_and_events_fires_for_each_of_them()
    {
        var page = NewName("page");
        var post = NewName("post");
        var workflowId = await StoreWorkflowAsync(w =>
        {
            w.TriggerContentTypes = [page, post];
            w.TriggerEvents = [WorkflowEvents.Published, WorkflowEvents.Unpublished];
        });

        (await EnqueueAsync(page, WorkflowEvents.Published)).Should().Be(1);
        (await EnqueueAsync(post, WorkflowEvents.Unpublished)).Should().Be(1);
        (await EnqueueAsync(post, WorkflowEvents.Updated)).Should().Be(0, "Updated is not in the list");
        (await EnqueueAsync(NewName("other"), WorkflowEvents.Published)).Should().Be(0, "that type is not in the list");

        var runs = await RunsOfAsync(workflowId);
        runs.Should().HaveCount(2);
        runs.Select(r => r.ContentType).Should().BeEquivalentTo(new[] { page, post });
        runs.Select(r => r.TriggerEvent).Should().BeEquivalentTo(new[] { WorkflowEvents.Published, WorkflowEvents.Unpublished });
    }

    [Fact]
    public async Task An_event_matching_two_entries_in_the_lists_queues_one_run()
    {
        var post = NewName("post");

        // Stored straight through a session, so nothing has removed the repeats. The single fields
        // are left blank, so only the lists can match.
        var workflowId = await StoreWorkflowAsync(w =>
        {
            w.TriggerContentTypes = [post, post];
            w.TriggerEvents = [WorkflowEvents.Published, WorkflowEvents.Published];
        });

        (await EnqueueAsync(post, WorkflowEvents.Published)).Should().Be(1);

        (await RunsOfAsync(workflowId)).Should().HaveCount(1, "one event is one run of a workflow");
    }

    [Fact]
    public async Task A_definition_stored_before_the_lists_existed_still_fires_on_its_single_type_and_event()
    {
        var post = NewName("post");
        var workflowId = await StoreWorkflowAsync(w =>
        {
            w.TriggerContentType = post;
            w.TriggerEvent = WorkflowEvents.Published;
        });
        await StripListsFromStoredDocumentAsync(workflowId);

        (await EnqueueAsync(post, WorkflowEvents.Updated)).Should().Be(0, "it listens for Published only");
        (await EnqueueAsync(post, WorkflowEvents.Published)).Should().Be(1);

        var runs = await RunsOfAsync(workflowId);
        runs.Should().HaveCount(1);
        runs[0].TriggerEvent.Should().Be(WorkflowEvents.Published);
    }

    [Fact]
    public async Task Creating_a_workflow_with_a_list_of_events_returns_the_list_and_its_first_event_as_the_single_field()
    {
        await AuthenticateAsAdminAsync();

        var res = await PostWorkflowAsync(new
        {
            name = NewName("0663-wf"),
            triggerContentType = NewName("post"),
            triggerEvents = new[] { "Published", "Unpublished", "Published" },
        });
        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", res.StatusCode, await res.Content.ReadAsStringAsync());
        var created = await ReadAsync(res);

        EventsOf(created).Should().Equal(["Published", "Unpublished"], "a repeated event is stored once");
        created.GetProperty("triggerEvent").GetString().Should().Be("Published",
            "a reader that only knows the single field still sees an event the workflow fires on");
    }

    [Fact]
    public async Task A_single_event_workflow_reads_back_unchanged_with_its_event_in_the_list()
    {
        await AuthenticateAsAdminAsync();

        var res = await PostWorkflowAsync(new
        {
            name = NewName("0663-wf"),
            triggerContentType = NewName("post"),
            triggerEvent = "Deleted",
        });
        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", res.StatusCode, await res.Content.ReadAsStringAsync());
        var created = await ReadAsync(res);

        created.GetProperty("triggerEvent").GetString().Should().Be("Deleted");
        EventsOf(created).Should().Equal(["Deleted"]);
    }

    [Fact]
    public async Task An_unknown_event_in_the_list_is_refused()
    {
        await AuthenticateAsAdminAsync();

        var res = await PostWorkflowAsync(new
        {
            name = NewName("0663-wf"),
            triggerContentType = NewName("post"),
            triggerEvents = new[] { "Published", "Vanished" },
        });
        var body = await res.Content.ReadAsStringAsync();

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, "got {0}: {1}", res.StatusCode, body);
        body.Should().Contain("triggerEvents[1]: Trigger event must be one of");
    }

    private async Task AuthenticateAsAdminAsync()
    {
        var (token, _) = await TestHelpers.CreateAdminUserAsync(_factory);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private async Task AuthenticateAsync(params string[] roles)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var roleIds = new List<Guid>();
        foreach (var roleName in roles)
        {
            var role = await session.Query<Role>().FirstOrDefaultAsync(r => r.Name == roleName);
            if (role is null)
            {
                role = new Role { Id = Guid.NewGuid(), Name = roleName };
                session.Store(role);
            }

            roleIds.Add(role.Id);
        }

        var userId = Guid.NewGuid();
        session.Store(new User
        {
            Id = userId,
            Username = $"trigger-{userId:n}",
            Email = $"trigger-{userId:n}@example.com",
            RoleIds = roleIds,
        });
        await session.SaveChangesAsync();

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: roles, userId: userId.ToString()));
    }

    private static string NewName(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private static WorkflowAction EmailAction() => new()
    {
        Type = "Email",
        Parameters = new Dictionary<string, string> { ["To"] = "a@example.com", ["Subject"] = "s", ["Body"] = "b" },
    };

    private async Task<Guid> StoreWorkflowAsync(Action<WorkflowDefinition> trigger)
    {
        var workflow = new WorkflowDefinition
        {
            Id = Guid.NewGuid(),
            Name = NewName("0663-wf"),
            // Refused before any socket is opened, so a run the runner picks up sends nothing.
            Actions = [new WorkflowAction { Type = "Webhook", Parameters = new Dictionary<string, string> { ["Url"] = "not-a-url" } }],
        };
        trigger(workflow);

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(workflow);
        await session.SaveChangesAsync();

        return workflow.Id;
    }

    /// <summary>What the projection does for one event, without waiting for the daemon.</summary>
    private async Task<int> EnqueueAsync(string contentType, string eventType)
    {
        using var scope = _factory.Services.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<IWorkflowRunQueue>();
        var content = new Content
        {
            Id = Guid.NewGuid(),
            ContentType = contentType,
            Data = new Dictionary<string, object>(),
        };

        return await queue.EnqueueAsync(content, eventType, Random.Shared.NextInt64(1, long.MaxValue), CancellationToken.None);
    }

    private async Task<IReadOnlyList<WorkflowRun>> RunsOfAsync(Guid workflowId)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return await session.Query<WorkflowRun>()
            .Where(r => r.WorkflowDefinitionId == workflowId)
            .ToListAsync();
    }

    /// <summary>
    /// Removes both lists from a stored workflow's JSON, so the document looks like one saved by 4.4.
    /// </summary>
    private async Task StripListsFromStoredDocumentAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        session.QueueSqlCommand(
            $"update {store.Options.DatabaseSchemaName}.mt_doc_workflowdefinition "
          + "set data = data - 'TriggerContentTypes' - 'triggerContentTypes' - 'TriggerEvents' - 'triggerEvents' "
          + "where id = ?",
            id);
        await session.SaveChangesAsync();

        var json = await session.Json.FindByIdAsync<WorkflowDefinition>(id);
        json.Should().NotBeNull();
        json.Should().Contain("riggerEvent", "the single field is still there");
        json.Should().NotContain("riggerEvents", "the list has to be gone for this to be an old document");
        json.Should().NotContain("riggerContentTypes", "the list has to be gone for this to be an old document");
    }

    /// <summary>
    /// Runs queued attempts until every run of these workflows is finished. Either this runner or
    /// the fixture's hosted one may claim an attempt.
    /// </summary>
    private async Task DrainAsync(params Guid[] workflowIds)
    {
        var runner = new WorkflowRunner(
            _factory.Services,
            _factory.Services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<WorkflowRunner>>(),
            _factory.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>());

        await PollAsync(
            async () =>
            {
                await runner.RunOnceAsync(CancellationToken.None);

                foreach (var workflowId in workflowIds)
                {
                    var queued = await RunsOfAsync(workflowId);
                    if (queued.Count == 0 || queued.Any(r => r.Status is RunStatus.Pending or RunStatus.Running))
                    {
                        return false;
                    }
                }

                return true;
            },
            "a queued run was never finished by a runner");
    }

    /// <summary>
    /// A conditional whose then branch is one DeletedEcho. The child is given its own RunId, since
    /// the runner's is not passed down to children, so the echo can be found by it.
    /// </summary>
    private static WorkflowAction Conditional(string condition, string childKey) => new()
    {
        Type = "Conditional",
        Parameters = new Dictionary<string, string>
        {
            ["Condition"] = condition,
            ["ThenActions"] = JsonSerializer.Serialize(new[]
            {
                new { Type = "DeletedEcho", Parameters = new Dictionary<string, string> { ["RunId"] = childKey } },
            }),
        },
    };

    private async Task<Guid> SeedAsync(string contentType, string needle, Guid? withId = null)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var writer = scope.ServiceProvider.GetRequiredService<barakoCMS.Core.Interfaces.IContentWriter>();
        var id = withId ?? Guid.NewGuid();

        await writer.CreateAsync(new barakoCMS.Events.ContentCreated(
            id, contentType, new Dictionary<string, object> { ["FullName"] = needle },
            ContentStatus.Draft, Guid.NewGuid(), needle, SensitivityLevel.Public), default);

        await session.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> CreateContentAsync(string contentType)
    {
        var response = await _client.PostAsJsonAsync("/api/contents", new
        {
            ContentType = contentType,
            Data = new Dictionary<string, object> { { "Title", "workflow subject" } },
        });
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<barakoCMS.Features.Content.Create.Response>(ApiJson.Options))!.Id;
    }

    private Task<HttpResponseMessage> ChangeStatusAsync(Guid id, ContentStatus status) =>
        _client.PutAsJsonAsync(
            $"/api/contents/{id}/status",
            new barakoCMS.Features.Content.ChangeStatus.Request { Id = id, NewStatus = status });

    private Task<HttpResponseMessage> PostWorkflowAsync(object trigger)
    {
        var body = JsonSerializer.SerializeToNode(trigger)!.AsObject();
        body["actions"] = JsonSerializer.SerializeToNode(new[] { EmailAction() });
        return _client.PostAsJsonAsync("/api/workflows", body);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage res) =>
        (await res.Content.ReadFromJsonAsync<JsonElement>()).Clone();

    private static List<string?> EventsOf(JsonElement workflow)
    {
        workflow.TryGetProperty("triggerEvents", out var events).Should().BeTrue(
            "the response has to carry the list: {0}", workflow.GetRawText());
        return events.EnumerateArray().Select(e => e.GetString()).ToList();
    }

    private static async Task PollAsync(Func<Task<bool>> condition, string because)
    {
        var deadline = DateTime.UtcNow + PollTimeout;

        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        throw new Xunit.Sdk.XunitException($"Timed out after {PollTimeout.TotalSeconds:0}s: {because}");
    }
}
