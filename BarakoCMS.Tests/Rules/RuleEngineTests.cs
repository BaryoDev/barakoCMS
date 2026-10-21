using barakoCMS.Models;
using FluentAssertions;
using static BarakoCMS.Tests.Rules.RuleKit;

namespace BarakoCMS.Tests.Rules;

[Collection("Sequential")]
public class RuleEngineTests(IntegrationTestFixture host)
{
    [Fact]
    public async Task Field_conditions_pass_and_fail()
    {
        var type = await TypeAsync(host, Rule("positive", Cond("Amount", "gt", "0")));

        (await RunAsync(host, type, Data(("Amount", 5)))).Passed.Should().BeTrue();
        var failed = await RunAsync(host, type, Data(("Amount", 0)));
        failed.Passed.Should().BeFalse();
        failed.Message.Should().Be("positive failed");
        failed.RuleName.Should().Be("positive");
    }

    [Fact]
    public async Task Numbers_compare_as_numbers_not_text()
    {
        var type = await TypeAsync(host, Rule("bigger", Cond("Amount", "gt", "9")));

        (await RunAsync(host, type, Data(("Amount", "10")))).Passed.Should().BeTrue();
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1,000")]
    [InlineData("1e3")]
    [InlineData(" 5")]
    public async Task Text_against_a_number_limit_fails(string amount)
    {
        var type = await TypeAsync(host, Rule("positive", Cond("Amount", "gt", "0")));

        (await RunAsync(host, type, Data(("Amount", amount)))).Passed.Should().BeFalse();
    }

    [Fact]
    public async Task Only_rules_for_the_event_run()
    {
        var onIssue = Rule("on-issue", Cond("Order", "notEmpty"));
        onIssue.When = RuleWhen.Transition;
        onIssue.Transition = "Issue";
        onIssue.InState = "Draft";
        var type = await TypeAsync(host, onIssue);
        var empty = Data();

        (await RunAsync(host, type, empty)).Passed.Should().BeTrue("a transition rule does not run on save");
        (await RunAsync(host, type, empty, RuleWhen.Transition, "Void", "Draft")).Passed.Should().BeTrue();
        (await RunAsync(host, type, empty, RuleWhen.Transition, "Issue", "Issued")).Passed.Should().BeTrue();
        (await RunAsync(host, type, empty, RuleWhen.Transition, "issue", "draft")).Passed.Should().BeFalse();
    }

    [Fact]
    public async Task Empty_and_notEmpty_and_in_and_negate()
    {
        var type = await TypeAsync(host,
            Rule("note-empty", Cond("Note", "empty")),
            Rule("method", Cond("Method", "in", "Cash, GCash")),
            Rule("not-void", Cond("Status", "eq", "Void", negate: true)),
            Rule("name", Cond("Name", "notEmpty")));

        (await RunAsync(host, type, Data(("Method", "gcash"), ("Status", "Open"), ("Name", "A")))).Passed.Should().BeTrue();
        (await RunAsync(host, type, Data(("Note", "x"), ("Method", "Cash"), ("Name", "A")))).RuleName.Should().Be("note-empty");
        (await RunAsync(host, type, Data(("Method", "Card"), ("Name", "A")))).RuleName.Should().Be("method");
        (await RunAsync(host, type, Data(("Method", "Cash"), ("Status", "void"), ("Name", "A")))).RuleName.Should().Be("not-void");
        (await RunAsync(host, type, Data(("Method", "Cash")))).RuleName.Should().Be("name");
    }

    [Fact]
    public async Task Set_writes_derived_fields_only_when_the_rule_passes()
    {
        var rule = Rule("total", Cond("Qty", "gt", "0"));
        rule.Set["Total"] = "{{Qty}}";
        var type = await TypeAsync(host, rule);

        var passing = Data(("Qty", 3));
        (await RunAsync(host, type, passing)).Passed.Should().BeTrue();
        passing["Total"].Should().Be("3");

        var failing = Data(("Qty", 0));
        (await RunAsync(host, type, failing)).Passed.Should().BeFalse();
        failing.Should().NotContainKey("Total");
    }

    [Fact]
    public async Task A_placeholder_for_a_missing_field_fills_empty()
    {
        var rule = Rule("copy");
        rule.Set["Label"] = "[{{Missing}}] by $CURRENT_USER";
        var type = await TypeAsync(host, rule);

        var data = Data();
        (await RunAsync(host, type, data)).Passed.Should().BeTrue();
        ((string)data["Label"]!).Should().StartWith("[] by ").And.NotContain("$CURRENT_USER");
    }

    [Fact]
    public async Task Unknown_kind_fails_with_a_config_message()
    {
        var type = await TypeAsync(host, Rule("odd", Cond("X", "eq", "1", kind: "nonsense")));

        var result = await RunAsync(host, type, Data(("X", "1")));
        result.Passed.Should().BeFalse();
        result.Message.Should().Contain("unknown condition kind 'nonsense'");
    }

    [Fact]
    public async Task First_failing_rule_wins_and_later_rules_do_not_set()
    {
        var later = Rule("later");
        later.Set["Stamp"] = "yes";
        var type = await TypeAsync(host, Rule("first", Cond("A", "notEmpty")), later);

        var data = Data();
        var result = await RunAsync(host, type, data);
        result.RuleName.Should().Be("first");
        data.Should().NotContainKey("Stamp");
    }
}
