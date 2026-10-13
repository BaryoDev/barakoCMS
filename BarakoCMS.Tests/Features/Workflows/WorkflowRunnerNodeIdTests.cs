using barakoCMS.Features.Workflows;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// The id a runner leases attempts under: at most 40 characters for any machine name, different
/// for every runner, and the same for one runner every time it is read.
/// </summary>
public class WorkflowRunnerNodeIdTests
{
    [Theory]
    [InlineData("")]
    [InlineData("vm")]
    [InlineData("web1")]
    [InlineData("a-machine-name-that-is-exactly-sixty-characters-long-0123456")]
    public void The_node_id_fits_the_lease_column_for_any_machine_name(string machineName)
    {
        var id = WorkflowRunner.NodeId(machineName);

        id.Length.Should().BeInRange(1, 40);
        id.Should().MatchRegex("-[0-9a-f]{32}$", "the process's own part is never cut");
    }

    [Theory]
    [InlineData("vm")]
    [InlineData("a-machine-name-that-is-exactly-sixty-characters-long-0123456")]
    public void Two_runners_on_one_machine_get_different_ids(string machineName)
    {
        WorkflowRunner.NodeId(machineName).Should().NotBe(WorkflowRunner.NodeId(machineName));
    }

    [Fact]
    public void The_sixty_character_name_is_cut_and_the_two_character_name_is_kept()
    {
        "a-machine-name-that-is-exactly-sixty-characters-long-0123456".Length.Should().Be(60);

        WorkflowRunner.NodeId("vm").Should().StartWith("vm-");
        WorkflowRunner.NodeId("a-machine-name-that-is-exactly-sixty-characters-long-0123456")
            .Should().StartWith("a-machi");
    }

    [Fact]
    public void One_runner_reads_the_same_id_every_time_and_another_runner_does_not()
    {
        var config = new ConfigurationBuilder().Build();
        var one = new WorkflowRunner(null!, NullLogger<WorkflowRunner>.Instance, config);
        var other = new WorkflowRunner(null!, NullLogger<WorkflowRunner>.Instance, config);

        one.Node.Should().NotBeNullOrEmpty();
        one.Node.Should().Be(one.Node);
        one.Node.Should().NotBe(other.Node);
    }
}
