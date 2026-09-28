using barakoCMS.Features.Workflows;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// One workflow runner polls the shared database: the fixture's own.
/// </summary>
/// <remarks>
/// A derived host that also ran one could claim a run a test queued on the fixture and send it
/// through its own transport, so the test waited on a recorder that never saw the message.
/// </remarks>
[Collection("Sequential")]
public class IntegrationTestFixtureRunnerTests(IntegrationTestFixture factory)
{
    [Fact]
    public void Only_the_fixture_host_runs_the_workflow_runner()
    {
        var derived = factory.WithWebHostBuilder(_ => { });

        factory.Services.GetServices<IHostedService>().OfType<WorkflowRunner>()
            .Should().ContainSingle("the fixture's host drains the queue");
        derived.Services.GetServices<IHostedService>().OfType<WorkflowRunner>()
            .Should().BeEmpty("a derived host would compete for the fixture's runs");
    }
}
