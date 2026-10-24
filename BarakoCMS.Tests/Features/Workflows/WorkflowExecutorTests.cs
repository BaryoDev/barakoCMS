using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Features.Workflows;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>An executor that runs nothing, registered on the fixture so a workflow can name one other than the built-in runner.</summary>
internal sealed class ElsewhereExecutor : IWorkflowExecutor
{
    public const string Name = "elsewhere";

    string IWorkflowExecutor.Name => Name;
}

/// <summary>
/// A workflow names what runs it: the built-in runner by default, and only the runs routed to it.
/// </summary>
[Collection("Integration2")]
public class WorkflowExecutorTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly WorkflowStopHarness _harness;

    public WorkflowExecutorTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _harness = new WorkflowStopHarness(factory);
    }

    private static CancellationToken Ct => WorkflowStopHarness.Ct;

    [Fact]
    public async Task The_built_in_runner_leaves_a_run_routed_to_another_executor_and_runs_its_own()
    {
        var contentType = WorkflowStopHarness.NewName("post");
        var builtIn = await _harness.StoreWorkflowAsync(w => w.TriggerContentType = contentType);
        var elsewhere = await _harness.StoreWorkflowAsync(w =>
        {
            w.TriggerContentType = contentType;
            w.Executor = ElsewhereExecutor.Name;
        });
        var contentId = await _harness.StoreContentAsync();

        WorkflowRun mine = null!;
        WorkflowRun theirs = null!;

        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            using (var scope = _factory.Services.CreateScope())
            {
                var queue = scope.ServiceProvider.GetRequiredService<IWorkflowRunQueue>();
                (await queue.EnqueueAsync(new Content { Id = contentId, ContentType = contentType, Data = new() },
                    WorkflowEvents.Published, Random.Shared.NextInt64(1, long.MaxValue), Ct)).Should().Be(2);
            }

            await _harness.DrainAsync();

            mine = (await _harness.RunsOfAsync(builtIn)).Single();
            theirs = (await _harness.RunsOfAsync(elsewhere)).Single();
        });

        mine.Executor.Should().BeNull("a run for the built-in runner reads as every run did before");
        mine.Status.Should().Be(RunStatus.Succeeded);

        theirs.Executor.Should().Be(ElsewhereExecutor.Name);
        theirs.Status.Should().Be(RunStatus.Pending);
        theirs.Actions.Should().ContainSingle().Which.Attempts.Should().Be(0, "the built-in runner never claimed it");
        WorkflowStopHarness.TimesRun(theirs.Actions[0]).Should().Be(0);
    }

    [Fact]
    public async Task Another_executor_reads_its_own_due_runs_and_none_of_the_built_in_ones()
    {
        var builtIn = await _harness.StoreWorkflowAsync();
        var elsewhere = await _harness.StoreWorkflowAsync(w => w.Executor = ElsewhereExecutor.Name);
        var contentId = await _harness.StoreContentAsync();

        var parked = await _harness.SeedRunAsync(builtIn, contentId, [WorkflowStopHarness.Waiting()]);
        var routed = await _harness.SeedRunAsync(elsewhere, contentId, [WorkflowStopHarness.Waiting()]);

        await using (var session = _harness.Store.LightweightSession())
        {
            var run = await session.LoadAsync<WorkflowRun>(routed.Id, Ct);
            run!.Executor = ElsewhereExecutor.Name;
            session.Update(run);
            await session.SaveChangesAsync(Ct);
        }

        var later = DateTimeOffset.UtcNow.AddHours(2);
        await using var query = _harness.Store.QuerySession();
        var ids = new[] { parked.Id, routed.Id };

        (await query.Query<WorkflowRun>().Where(WorkflowExecutors.DueFor(ElsewhereExecutor.Name, later))
            .Where(r => r.Id.IsOneOf(ids)).Select(r => r.Id).ToListAsync(Ct))
            .Should().Equal(routed.Id);

        (await query.Query<WorkflowRun>().Where(WorkflowExecutors.DueFor(WorkflowExecutors.BuiltIn, later))
            .Where(r => r.Id.IsOneOf(ids)).Select(r => r.Id).ToListAsync(Ct))
            .Should().Equal(parked.Id);
    }

    [Fact]
    public async Task A_workflow_saved_without_an_executor_runs_on_the_built_in_one_and_an_unknown_one_is_refused()
    {
        var admin = await _harness.AdminAsync();

        var plain = await SaveAsync(admin, executor: null);
        var text = await plain.Content.ReadAsStringAsync(Ct);
        plain.StatusCode.Should().Be(HttpStatusCode.OK, text);
        using (var doc = JsonDocument.Parse(text))
        {
            doc.RootElement.GetProperty("executor").ValueKind.Should().Be(JsonValueKind.Null);
        }

        var named = await SaveAsync(admin, executor: ElsewhereExecutor.Name);
        text = await named.Content.ReadAsStringAsync(Ct);
        named.StatusCode.Should().Be(HttpStatusCode.OK, text);
        using (var doc = JsonDocument.Parse(text))
        {
            doc.RootElement.GetProperty("executor").GetString().Should().Be(ElsewhereExecutor.Name);
            (await _harness.LoadWorkflowAsync(doc.RootElement.GetProperty("id").GetGuid()))!.Executor
                .Should().Be(ElsewhereExecutor.Name);
        }

        var unknown = await SaveAsync(admin, executor: "temporal");
        unknown.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await unknown.Content.ReadAsStringAsync(Ct)).Should().Contain("executor");
    }

    private static Task<HttpResponseMessage> SaveAsync(HttpClient client, string? executor) =>
        client.PostAsJsonAsync("/api/workflows", new
        {
            name = WorkflowStopHarness.NewName("698-wf"),
            triggerContentType = WorkflowStopHarness.NewName("post"),
            triggerEvent = "Published",
            executor,
            actions = new object[] { new { type = WorkflowStopHarness.Counting } },
        }, Ct);
}
