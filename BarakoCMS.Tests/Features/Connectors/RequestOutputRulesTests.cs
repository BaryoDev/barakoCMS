using barakoCMS.Features.Requests;
using barakoCMS.Features.Workflows;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests.Features.Connectors;

/// <summary>
/// What a request definition may declare as outputs (#1201), checked when it is saved.
/// </summary>
public class RequestOutputRulesTests
{
    private static SaveRequestRequest Valid() => new()
    {
        Name = "Ask the model",
        Slug = "ask-model",
        ConnectorSlug = "ai",
        PathTemplate = "/v1/chat",
    };

    [Fact]
    public void A_definition_with_no_outputs_is_valid_as_before()
    {
        RequestRules.Check(Valid()).Should().BeNull();
    }

    [Fact]
    public void A_named_output_with_a_plain_path_is_valid()
    {
        var req = Valid();
        req.Outputs = new() { ["plan"] = "$.choices[0].message.content" };
        req.MissingOutput = "Empty";
        req.MaxOutputLength = 500;

        RequestRules.Check(req).Should().BeNull();
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("dash-ed")]
    [InlineData("")]
    public void An_output_name_a_later_step_cannot_read_is_refused(string name)
    {
        var req = Valid();
        req.Outputs = new() { [name] = "$.a" };

        RequestRules.Check(req).Should().Contain("Outputs");
    }

    [Fact]
    public void An_output_path_that_is_not_plain_is_refused_naming_the_output()
    {
        var req = Valid();
        req.Outputs = new() { ["plan"] = "$..content" };

        RequestRules.Check(req).Should().Contain("plan");
    }

    [Fact]
    public void More_outputs_than_one_action_may_keep_are_refused()
    {
        var req = Valid();
        req.Outputs = Enumerable.Range(0, ActionOutputs.MaxCaptured + 1).ToDictionary(i => "o" + i, _ => "$.a");

        RequestRules.Check(req).Should().Contain(ActionOutputs.MaxCaptured.ToString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void An_output_length_cap_below_one_is_refused(int max)
    {
        var req = Valid();
        req.Outputs = new() { ["plan"] = "$.a" };
        req.MaxOutputLength = max;

        RequestRules.Check(req).Should().Contain("MaxOutputLength");
    }

    [Fact]
    public void An_output_length_cap_above_what_a_run_keeps_is_refused()
    {
        var req = Valid();
        req.Outputs = new() { ["plan"] = "$.a" };
        req.MaxOutputLength = ActionOutputs.MaxValueLength + 1;

        RequestRules.Check(req).Should().Contain("MaxOutputLength").And.Contain(ActionOutputs.MaxValueLength.ToString());
    }

    [Fact]
    public void A_missing_value_rule_that_is_neither_fail_nor_empty_is_refused()
    {
        var req = Valid();
        req.Outputs = new() { ["plan"] = "$.a" };
        req.MissingOutput = "Ignore";

        RequestRules.Check(req).Should().Contain("MissingOutput");
    }
}
