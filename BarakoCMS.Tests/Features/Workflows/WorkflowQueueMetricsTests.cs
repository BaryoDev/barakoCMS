using System.Net;
using System.Net.Http.Headers;
using System.Text;
using barakoCMS.Features.Workflows;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// Runs are counted as they are queued, under the kind of event that fired them.
/// </summary>
/// <remarks>
/// The queue counts on the registry the host publishes, which every test in the process moves, so
/// each test reads its series before and after and asserts the difference. The projection can queue
/// a run for another test's event in between, so the difference is at least what the test queued.
/// </remarks>
[Collection("Sequential")]
public class WorkflowQueueMetricsTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly WorkflowStopHarness _harness;

    public WorkflowQueueMetricsTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _harness = new WorkflowStopHarness(factory);
    }

    private static CancellationToken Ct => WorkflowStopHarness.Ct;

    /// <summary>
    /// A transition is named by whoever configured the content type, so it is counted as
    /// "transition" and its name is not a label value.
    /// </summary>
    [Fact]
    public async Task A_run_queued_for_a_transition_is_counted_as_a_transition_and_its_name_is_not_published()
    {
        var contentType = WorkflowStopHarness.NewName("post");
        var transition = WorkflowStopHarness.NewName("Approve");
        var trigger = WorkflowEvents.TransitionPrefix + transition;

        await _harness.StoreWorkflowAsync(w =>
        {
            w.TriggerContentType = contentType;
            w.TriggerEvent = trigger;
        });

        var before = WorkflowMetrics.Default.RunsQueued.WithLabels("transition").Value;

        using (var scope = _factory.Services.CreateScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<IWorkflowRunQueue>();
            var content = new Content
            {
                Id = Guid.NewGuid(),
                ContentType = contentType,
                Data = new Dictionary<string, object>(),
            };

            (await queue.EnqueueAsync(content, trigger, Random.Shared.NextInt64(1, long.MaxValue), Ct)).Should().Be(1);
        }

        (WorkflowMetrics.Default.RunsQueued.WithLabels("transition").Value - before).Should().BeGreaterThanOrEqualTo(1);

        using var stream = new MemoryStream();
        await Prometheus.Metrics.DefaultRegistry.CollectAndExportAsTextAsync(stream, Ct);
        var published = Encoding.UTF8.GetString(stream.ToArray());

        published.Should().Contain("barakocms_workflow_runs_queued_total");
        published.Should().NotContain(transition);
        published.Should().NotContain(contentType);
    }

    /// <summary>
    /// An erasure queues its Deleted runs on the endpoint's own session, and they are counted once
    /// that session has committed.
    /// </summary>
    [Fact]
    public async Task A_run_queued_by_an_erasure_is_counted_as_deleted()
    {
        var contentType = WorkflowStopHarness.NewName("post");
        var workflowId = await _harness.StoreWorkflowAsync(w =>
        {
            w.TriggerContentType = contentType;
            w.TriggerEvent = WorkflowEvents.Deleted;
        });
        var id = await CreateEntryAsync(contentType);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await _factory.StoredUserTokenAsync("SuperAdmin"));

        var before = WorkflowMetrics.Default.RunsQueued.WithLabels("deleted").Value;

        (await client.DeleteAsync($"/api/contents/{id}/erase", Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await _harness.RunsOfAsync(workflowId)).Should().HaveCount(1, "the count below is of a run that was queued");
        (WorkflowMetrics.Default.RunsQueued.WithLabels("deleted").Value - before).Should().BeGreaterThanOrEqualTo(1);
    }

    private async Task<Guid> CreateEntryAsync(string contentType)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var writer = scope.ServiceProvider.GetRequiredService<barakoCMS.Core.Interfaces.IContentWriter>();
        var id = Guid.NewGuid();

        await writer.CreateAsync(new barakoCMS.Events.ContentCreated(
            id, contentType, new Dictionary<string, object> { ["Title"] = "to be erased" },
            ContentStatus.Draft, Guid.NewGuid(), "to-be-erased", SensitivityLevel.Public), Ct);

        await session.SaveChangesAsync(Ct);
        return id;
    }
}
