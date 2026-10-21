using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Rules;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using static BarakoCMS.Tests.Rules.RuleKit;
using static BarakoCMS.Tests.Rules.RuleRows;

namespace BarakoCMS.Tests.Rules;

[Collection("Sequential")]
public class RuleLetTests(IntegrationTestFixture host)
{
    private sealed class FixedLookup(string name, RuleLookupValue value) : IRuleLookup
    {
        public string Name => name;
        public Task<RuleLookupValue> ResolveAsync(IReadOnlyDictionary<string, string> args, IDocumentSession session, CancellationToken ct) =>
            Task.FromResult(value);
    }

    private static string Fill(string template, Dictionary<string, object?> data) =>
        RuleTemplates.Fill(template, data, Guid.Empty, new Dictionary<string, RuleLookupValue>(), out _, RuleValues.Utc(TimeProvider.System));

    private static Dictionary<string, object?> Line(decimal amount, string? code = null) =>
        code is null ? new() { ["Amount"] = amount } : new() { ["Amount"] = amount, ["Code"] = code };

    [Fact]
    public void Expressions_do_arithmetic()
    {
        var data = Data(("Qty", 3m), ("Price", 10.025m), ("Payer", "Company"));

        Fill("{{= round(Qty * Price * 0.2, 2) }}", data).Should().Be("6.02");
        Fill("{{= max(0, 5 - 9) }}", data).Should().Be("0");
        Fill("{{= -2 + 3 * (4 - 1) }}", data).Should().Be("7");
        Fill("{{= ifeq(Payer, 'company', 0, 12.5) }}", data).Should().Be("0");
    }

    [Fact]
    public void Sum_adds_every_value_a_path_reaches()
    {
        var data = Data(("Lines", new List<object?> { Line(100m), Line(250.50m), Line(40m, "HMODENY") }));

        decimal.Parse(Fill("{{= sum(Lines.Amount) }}", data), System.Globalization.CultureInfo.InvariantCulture).Should().Be(390.5m);
        decimal.Parse(Fill("{{= sum(Lines[Code=HMODENY].Amount) }}", data), System.Globalization.CultureInfo.InvariantCulture).Should().Be(40m);
    }

    [Theory]
    [InlineData("{{= Qty / 0 }}", "divides by zero")]
    [InlineData("{{= Qty + Name }}", "is not a number")]
    [InlineData("{{= nope(1) }}", "unknown function")]
    [InlineData("{{= round(Qty, 99) }}", "whole number from 0 to 28")]
    [InlineData("{{= 79228162514264337593543950335 * Qty }}", "too large")]
    [InlineData("{{= 1,000 + Qty }}", "unexpected ','")]
    public async Task A_bad_expression_fails_the_rule(string value, string why)
    {
        var type = await TypeAsync(host, Rule("calc", Cond("Total", "eq", value)));

        var result = await RunAsync(host, type, Data(("Qty", "2"), ("Name", "abc"), ("Total", "1")));

        result.Passed.Should().BeFalse();
        result.Message.Should().Contain(why);
    }

    [Fact]
    public async Task Deep_nesting_fails_the_rule()
    {
        var type = await TypeAsync(host, Rule("calc", Cond("Total", "eq", "{{= " + new string('(', 100) + "1" + new string(')', 100) + " }}")));

        (await RunAsync(host, type, Data(("Total", "1")))).Message.Should().Contain("nests too deeply");
    }

    [Fact]
    public async Task Lets_run_in_order_and_feed_conditions_and_messages()
    {
        var payment = $"pay{Guid.NewGuid():n}"[..20];
        await StoreAsync(host, payment, new() { ["Receipt"] = "R1", ["Amount"] = "100" });
        await StoreAsync(host, payment, new() { ["Receipt"] = "R1", ["Amount"] = "200" });
        var rule = Rule("left", Cond("Amount", "lte", "{{let.left}}"));
        rule.Let =
        [
            new() { Kind = "sum", ContentType = payment, Where = "Receipt=R1", Sum = "Amount", As = "paid" },
            new() { Kind = "value", Value = "{{= Total - let.paid }}", As = "left" },
        ];
        rule.Message = "Only {{let.left}} is left.";
        var type = await TypeAsync(host, rule);

        (await RunAsync(host, type, Data(("Total", "500"), ("Amount", "150")))).Passed.Should().BeTrue();
        var refused = await RunAsync(host, type, Data(("Total", "500"), ("Amount", "250")));
        refused.Passed.Should().BeFalse();
        refused.Message.Should().Be("Only 200 is left.");
    }

    [Fact]
    public async Task Let_reads_a_field_and_a_lookup()
    {
        var rule = Rule("stock", Cond("Name", "eq", "never"));
        rule.Let =
        [
            new() { Kind = "field", Field = "Name", As = "n" },
            new() { Kind = "lookup", Lookup = "stock.left", As = "left" },
        ];
        rule.Message = "{{let.n}} has {{let.left}}.";
        var type = await TypeAsync(host, rule);

        var result = await RunAsync(host, type, Data(("Name", "Pills")), lookups: [new FixedLookup("stock.left", new(true, null, 5))]);

        result.Message.Should().Be("Pills has 5.");
    }

    [Theory]
    [InlineData(null, "value", "A let needs a name in 'as'.")]
    [InlineData("x", "each", "Let 'x' cannot use kind 'each'.")]
    public async Task A_let_without_a_name_or_with_a_bad_kind_fails_the_rule(string? name, string kind, string message)
    {
        var rule = Rule("bad");
        rule.Let = [new() { Kind = kind, As = name, Value = "1" }];
        var type = await TypeAsync(host, rule);

        var result = await RunAsync(host, type, Data());

        result.Passed.Should().BeFalse();
        result.Message.Should().Be(message);
    }

    [Fact]
    public async Task Stored_compares_the_saved_value()
    {
        var rule = Rule("lock", Cond("Name", "empty", kind: "stored"));
        rule.If = [Cond("Name", "eq", kind: "changed")];
        rule.Message = "locked";
        var type = await TypeAsync(host, rule);
        var existing = new Content { Id = Guid.NewGuid(), ContentType = type, Data = new() { ["Name"] = "Maxicare" } };

        var renamed = await RunAsync(host, type, Data(("Name", "Maxicare Inc")), existing: existing);
        renamed.Passed.Should().BeFalse();
        renamed.Message.Should().Be("locked");
        (await RunAsync(host, type, Data(("Name", "Maxicare")), existing: existing)).Passed.Should().BeTrue();
        (await RunAsync(host, type, Data(("Name", "Anything")))).Passed.Should().BeTrue();
    }
}
