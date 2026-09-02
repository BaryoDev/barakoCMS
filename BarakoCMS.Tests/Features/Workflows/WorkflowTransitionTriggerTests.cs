using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// A workflow fires on a named transition, and an edit does not fire it.
/// </summary>
/// <remarks>
/// TriggerEvent was Created or Updated, and "when an invoice becomes Approved" is neither. Routing
/// on Updated fires on every save, so the supplier is notified on every edit before approval and
/// again after, which is the feature not existing rather than a rough edge.
///
/// Every assertion that something did not fire is paired with one that it still fires when it
/// should, in the same test where the two share a workflow. A projection that fires nothing passes
/// every negative in this file on its own.
/// </remarks>
[Collection("Sequential")]
public class WorkflowTransitionTriggerTests
{
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(30);

    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;

    // Transitions go through a second person. #341 refuses a transition by whoever raised the entry,
    // administrator included, so the creator moving its own invoice on is a 403 and every firing
    // assertion here would be waiting on an event that was never appended.
    private readonly HttpClient _approver;

    public WorkflowTransitionTriggerTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _approver = factory.CreateClient();
    }

    /// <summary>
    /// The pair that proves the feature: an edit does not fire the Approve workflow, and the Approve
    /// transition does.
    /// </summary>
    /// <remarks>
    /// Both halves in one test on purpose. Asserting the edit fired nothing is satisfied by a
    /// projection that handles no transition at all, which is the state this started from.
    /// </remarks>
    [Fact]
    public async Task An_edit_does_not_fire_an_approval_workflow_and_the_approval_does()
    {
        await AuthenticateAsync();
        var type = await TypeWithLifecycleAsync();
        var probe = NewName("probe");
        await StoreWorkflowAsync(type, WorkflowEvents.ForTransition("Approve"), probe);

        var id = await CreateContentAsync(type);
        await TransitionAsync(id, "Submit");

        (await EditAsync(id)).EnsureSuccessStatusCode();
        await Task.Delay(TimeSpan.FromSeconds(3));
        (await CountAsync(probe)).Should().Be(0,
            "routing an approval on Updated is what sends the supplier the invoice on every edit");

        (await TransitionAsync(id, "Approve")).EnsureSuccessStatusCode();

        (await WaitForAsync(probe, atLeast: 1)).Should().Be(1, "the transition it names is what fires it");
    }

    /// <summary>
    /// The other direction: a transition is not an edit, so it does not fire the Updated workflows.
    /// </summary>
    [Fact]
    public async Task A_transition_does_not_fire_an_Updated_workflow_and_an_edit_does()
    {
        await AuthenticateAsync();
        var type = await TypeWithLifecycleAsync();
        var probe = NewName("probe");
        await StoreWorkflowAsync(type, WorkflowEvents.Updated, probe);

        var id = await CreateContentAsync(type);

        (await TransitionAsync(id, "Submit")).EnsureSuccessStatusCode();
        await Task.Delay(TimeSpan.FromSeconds(3));
        (await CountAsync(probe)).Should().Be(0, "a transition changed no field, so nothing was updated");

        (await EditAsync(id)).EnsureSuccessStatusCode();

        (await WaitForAsync(probe, atLeast: 1)).Should().Be(1, "existing Updated workflows are unaffected");
    }

    /// <summary>
    /// A workflow naming a transition its content type does not declare is refused when saved.
    /// </summary>
    /// <remarks>
    /// Refused rather than stored and never fired, because a workflow that never fires looks exactly
    /// like one that fires and fails, and save time is the only moment the mistake is cheap.
    /// </remarks>
    [Fact]
    public async Task A_workflow_naming_an_undeclared_transition_is_refused_when_saved()
    {
        await AuthenticateAsync();
        var type = await TypeWithLifecycleAsync();

        var res = await PostWorkflowAsync(type, WorkflowEvents.ForTransition("Escalate"), NewName("probe"));
        var body = await res.Content.ReadAsStringAsync();

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, "got {0}: {1}", res.StatusCode, body);
        body.Should().Contain("Escalate", "the message has to name the transition that was not found");
        body.Should().Contain("Approve", "and what the type does declare, which is how the typo gets fixed");
    }

    /// <summary>
    /// The control. Without it a validator that refused every transition trigger would pass the test
    /// above while making the feature unreachable.
    /// </summary>
    [Fact]
    public async Task A_workflow_naming_a_declared_transition_is_accepted()
    {
        await AuthenticateAsync();
        var type = await TypeWithLifecycleAsync();

        var res = await PostWorkflowAsync(type, WorkflowEvents.ForTransition("Approve"), NewName("probe"));

        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", res.StatusCode, await res.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// A trigger saved in another casing is stored the way the type declares it, and fires.
    /// </summary>
    /// <remarks>
    /// The engine matches TriggerEvent with an equality query and the lifecycle matches a transition
    /// name case insensitively. Storing what the caller sent would accept "transition:approve"
    /// against a transition declared "Approve" and then never fire it, which is the same failure the
    /// validation above exists to prevent, reached by a different road.
    ///
    /// The firing assertion is the one that matters. Asserting only the stored string would pass
    /// against a normalisation that wrote a spelling the projection never emits.
    /// </remarks>
    [Fact]
    public async Task A_trigger_saved_in_another_casing_is_stored_as_declared_and_fires()
    {
        await AuthenticateAsync();
        var type = await TypeWithLifecycleAsync();
        var probe = NewName("probe");

        var res = await PostWorkflowAsync(type, "transition:approve", probe);
        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", res.StatusCode, await res.Content.ReadAsStringAsync());

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
            var stored = await session.Query<WorkflowDefinition>()
                .FirstOrDefaultAsync(w => w.TriggerContentType == type);

            stored.Should().NotBeNull();
            stored!.TriggerEvent.Should().Be("transition:Approve");
        }

        var id = await CreateContentAsync(type);
        await TransitionAsync(id, "Submit");
        (await TransitionAsync(id, "Approve")).EnsureSuccessStatusCode();

        (await WaitForAsync(probe, atLeast: 1)).Should().Be(1);
    }

    /// <summary>
    /// A trigger naming a type that does not exist is refused rather than passed over.
    /// </summary>
    /// <remarks>
    /// Skipping the check when the thing to check against is missing is how a validation quietly
    /// stops validating, and here it lets through the one workflow that can never fire.
    /// </remarks>
    [Fact]
    public async Task A_transition_trigger_on_a_content_type_that_does_not_exist_is_refused()
    {
        await AuthenticateAsync();

        var res = await PostWorkflowAsync(NewName("ghost"), WorkflowEvents.ForTransition("Approve"), NewName("probe"));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "got {0}: {1}", res.StatusCode, await res.Content.ReadAsStringAsync());
    }

    private async Task AuthenticateAsync()
    {
        var (token, _) = await TestHelpers.CreateAdminUserAsync(_factory);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var (approverToken, _) = await TestHelpers.CreateAdminUserAsync(_factory);
        _approver.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", approverToken);
    }

    // One content type per test. The daemon is asynchronous, so one test's events can still be in
    // flight when the next registers a workflow against the same type.
    private static string NewName(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..20];

    private async Task<string> TypeWithLifecycleAsync()
    {
        var name = NewName("inv");
        var res = await _client.PostAsJsonAsync("/api/content-types", new
        {
            name,
            displayName = "Invoice",
            fields = new[] { new { name = "Title", type = "string" } },
            lifecycle = new LifecycleDefinition
            {
                States = ["Draft", "Submitted", "Approved"],
                InitialState = "Draft",
                Transitions =
                [
                    new StateTransition { Name = "Submit", From = "Draft", To = "Submitted" },
                    new StateTransition { Name = "Approve", From = "Submitted", To = "Approved" },
                ],
            },
        });
        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", res.StatusCode, await res.Content.ReadAsStringAsync());
        return name;
    }

    private async Task<Guid> CreateContentAsync(string contentType)
    {
        var res = await _client.PostAsJsonAsync("/api/contents", new
        {
            ContentType = contentType,
            Data = new Dictionary<string, object> { ["Title"] = "an invoice" },
        });
        res.EnsureSuccessStatusCode();

        return (await res.Content.ReadFromJsonAsync<barakoCMS.Features.Content.Create.Response>(ApiJson.Options))!.Id;
    }

    private Task<HttpResponseMessage> TransitionAsync(Guid id, string transition) =>
        _approver.PutAsJsonAsync($"/api/contents/{id}/status", new { id, transition });

    private Task<HttpResponseMessage> EditAsync(Guid id) =>
        _client.PutAsJsonAsync($"/api/contents/{id}", new
        {
            id,
            data = new Dictionary<string, object> { ["Title"] = $"edited {Guid.NewGuid():N}" },
        });

    private static WorkflowAction Probe(string probeContentType) => new()
    {
        Type = "CreateTask",
        Parameters = new Dictionary<string, string>
        {
            ["ContentType"] = probeContentType,
            ["Title"] = "probe",
        },
    };

    /// <summary>Stored straight through the session, so a firing test does not depend on validation.</summary>
    private async Task StoreWorkflowAsync(string triggerContentType, string triggerEvent, string probeContentType)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new WorkflowDefinition
        {
            Id = Guid.NewGuid(),
            Name = NewName("wf"),
            TriggerContentType = triggerContentType,
            TriggerEvent = triggerEvent,
            Actions = [Probe(probeContentType)],
        });
        await session.SaveChangesAsync();
    }

    /// <summary>Through the endpoint, which is where validation and normalisation happen.</summary>
    private Task<HttpResponseMessage> PostWorkflowAsync(string triggerContentType, string triggerEvent, string probeContentType) =>
        _client.PostAsJsonAsync("/api/workflows", new
        {
            name = NewName("wf"),
            triggerContentType,
            triggerEvent,
            actions = new[] { Probe(probeContentType) },
        });

    private async Task<int> CountAsync(string probeContentType)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();

        return await session.Query<barakoCMS.Models.Content>().CountAsync(c => c.ContentType == probeContentType);
    }

    /// <summary>
    /// Waits for at least <paramref name="atLeast"/> probes, then returns the count so the caller can
    /// assert it is not more than that.
    /// </summary>
    private async Task<int> WaitForAsync(string probeContentType, int atLeast)
    {
        var deadline = DateTime.UtcNow + PollTimeout;

        while (DateTime.UtcNow < deadline)
        {
            if (await CountAsync(probeContentType) >= atLeast)
            {
                // The daemon could still be producing a second one, which is the bug in the pair
                // above, so give it room rather than racing it.
                await Task.Delay(TimeSpan.FromSeconds(2));
                return await CountAsync(probeContentType);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        throw new Xunit.Sdk.XunitException(
            $"Timed out after {PollTimeout.TotalSeconds:0}s: the workflow never created a "
          + $"'{probeContentType}' item, and a workflow that fires nothing satisfies every negative "
          + "assertion in this class");
    }
}
