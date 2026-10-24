using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>Abandoning a run that failed part way, and cancelling a running one, each write one audit row naming the actor.</summary>
/// <remarks>
/// The abandon is seeded with a succeeded action beside the failed one, because abandoning a run in
/// which nothing succeeded is refused with 409 ("cancel it instead"). Runs are seeded with the hosted
/// runner paused so it cannot claim them before the request lands.
/// </remarks>
[Collection("Integration1")]
public class RunDismissalAuditTests(IntegrationTestFixture factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly WorkflowStopHarness _harness = new(factory);

    [Fact]
    public async Task Abandoning_a_failed_run_is_audited_with_the_actor()
    {
        var (admin, adminId) = await AdminWithIdAsync();
        var workflowId = await _harness.StoreWorkflowAsync();
        var contentId = await _harness.StoreContentAsync();
        var run = await _harness.SeedRunAsync(workflowId, contentId,
        [
            WorkflowStopHarness.Finished(AttemptStatus.Succeeded),
            WorkflowStopHarness.Finished(AttemptStatus.Failed),
        ]);

        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            var res = await admin.PostAsync($"/api/workflow-runs/{run.Id}/abandon", null, Ct);
            res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
        });

        await AssertOneRowAsync("workflow.run.abandoned", run.Id, adminId);
    }

    [Fact]
    public async Task Cancelling_a_running_run_is_audited_with_the_actor()
    {
        var (admin, adminId) = await AdminWithIdAsync();
        var workflowId = await _harness.StoreWorkflowAsync();
        var contentId = await _harness.StoreContentAsync();
        var run = await _harness.SeedRunAsync(workflowId, contentId,
        [
            WorkflowStopHarness.Finished(AttemptStatus.Succeeded),
            WorkflowStopHarness.Waiting(),
        ]);

        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            var res = await admin.PostAsync($"/api/workflow-runs/{run.Id}/cancel", null, Ct);
            res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
        });

        await AssertOneRowAsync("workflow.run.cancelled", run.Id, adminId);
    }

    private async Task AssertOneRowAsync(string action, Guid runId, Guid actorId)
    {
        var rows = await _harness.AuditOfAsync(action, runId);

        var row = rows.Should().ContainSingle().Subject;
        row.TargetType.Should().Be(nameof(WorkflowRun));
        row.TargetId.Should().Be(runId.ToString());
        row.ActorUserId.Should().Be(actorId);
    }

    /// <summary>A signed-in admin whose user id the test knows, so the audit row can be matched to it.</summary>
    private async Task<(HttpClient Client, Guid UserId)> AdminWithIdAsync()
    {
        var userId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new User
            {
                Id = userId,
                Username = $"dismiss-{userId:n}",
                Email = $"dismiss-{userId:n}@example.com",
                RoleIds = [SystemRoles.SuperAdminRoleId],
            });
            await session.SaveChangesAsync(Ct);
        }

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", factory.CreateToken(["SuperAdmin"], userId.ToString(),
                new Dictionary<string, string> { ["Username"] = $"dismiss-{userId:n}" }));
        return (client, userId);
    }
}
