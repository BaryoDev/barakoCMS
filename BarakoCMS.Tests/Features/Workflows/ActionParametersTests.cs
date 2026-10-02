using barakoCMS.Features.Workflows;
using barakoCMS.Models;
using FluentAssertions;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// Which parameters are resolved ahead of the action, for every caller at once: the engine, the
/// runner, the dry run and a conditional's children all go through <see cref="ActionParameters"/>.
/// </summary>
public class ActionParametersTests
{
    private static readonly Content Entry = new()
    {
        Id = Guid.NewGuid(),
        ContentType = "article",
        Status = ContentStatus.Published,
        Data = new Dictionary<string, object> { ["Note"] = "say \"Paid\" == yes" },
    };

    /// <summary>Issue #1050: the conditional reads the entry itself, so no value reaches its parser.</summary>
    [Theory]
    [InlineData("{{status}} == Published")]
    [InlineData("{{data.Note}} != \"Paid\"")]
    public void A_conditionals_condition_is_left_as_written(string condition)
    {
        var resolved = ActionParameters.Resolve(
            "Conditional", new Dictionary<string, string> { ["Condition"] = condition }, Entry);

        resolved.Should().HaveCount(1);
        resolved["Condition"].Should().Be(condition);
    }

    /// <summary>A guard that passes with or without the fix: only a Conditional keeps its condition.</summary>
    [Fact]
    public void A_parameter_named_condition_on_another_action_is_still_resolved()
    {
        var resolved = ActionParameters.Resolve(
            "Webhook", new Dictionary<string, string> { ["Condition"] = "{{status}} == Published" }, Entry);

        resolved.Should().HaveCount(1);
        resolved["Condition"].Should().Be("Published == Published");
    }
}
