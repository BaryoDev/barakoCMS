using barakoCMS.Features.WorkflowRuns;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// The per-status counts on the run list read at most a cap of runs per status, and stay inside the tenant.
/// </summary>
[Collection("Integration1")]
public class RunStatusCountTests
{
    private readonly IntegrationTestFixture _fixture;

    public RunStatusCountTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task SeedAsync(IDocumentStore store, string tenant, params (RunStatus Status, int Count)[] runs)
    {
        await using var session = store.LightweightSession(tenant);
        foreach (var (status, count) in runs)
        {
            for (var i = 0; i < count; i++)
            {
                session.Store(new WorkflowRun
                {
                    Id = Guid.NewGuid(),
                    WorkflowDefinitionId = Guid.NewGuid(),
                    ContentId = Guid.NewGuid(),
                    Status = status,
                });
            }
        }

        await session.SaveChangesAsync(Ct);
    }

    private static string NewTenant() => $"cnt-{Guid.NewGuid():N}"[..20];

    [Fact]
    public async Task Counts_are_exact_below_the_cap_and_stay_in_their_tenant()
    {
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var one = NewTenant();
        var two = NewTenant();
        await SeedAsync(store, one, (RunStatus.Pending, 2), (RunStatus.Succeeded, 3), (RunStatus.Failed, 1));
        await SeedAsync(store, two, (RunStatus.Pending, 5), (RunStatus.Cancelled, 4));

        await using var first = store.QuerySession(one);
        var a = await RunQueries.CountByStatusAsync(first, null, null, Ct);
        await using var second = store.QuerySession(two);
        var b = await RunQueries.CountByStatusAsync(second, null, null, Ct);

        a.Counts.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["Pending"] = 2, ["Running"] = 0, ["Succeeded"] = 3, ["Failed"] = 1, ["PartiallyFailed"] = 0, ["Cancelled"] = 0,
        });
        b.Counts.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["Pending"] = 5, ["Running"] = 0, ["Succeeded"] = 0, ["Failed"] = 0, ["PartiallyFailed"] = 0, ["Cancelled"] = 4,
        });
        a.Capped.Should().BeEmpty();
        b.Capped.Should().BeEmpty();
    }

    [Fact]
    public async Task A_status_past_the_cap_reports_the_cap_and_is_named()
    {
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var tenant = NewTenant();
        await SeedAsync(store, tenant, (RunStatus.Succeeded, 6), (RunStatus.Failed, 3), (RunStatus.Pending, 1));

        await using var session = store.QuerySession(tenant);
        var result = await RunQueries.CountByStatusAsync(session, null, null, Ct, cap: 3);

        result.Cap.Should().Be(3);
        result.Counts["Succeeded"].Should().Be(3);
        result.Counts["Failed"].Should().Be(3, "exactly the cap is not past it");
        result.Counts["Pending"].Should().Be(1);
        result.Capped.Should().BeEquivalentTo(["Succeeded"]);
    }
}
