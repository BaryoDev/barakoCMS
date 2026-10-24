using System.Diagnostics;
using System.Text.Json;
using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Rules;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using static BarakoCMS.Tests.Rules.RuleKit;

namespace BarakoCMS.Tests.Rules;

[Collection("Integration4")]
public class RuleFieldKindsTests(IntegrationTestFixture host)
{
    private async Task<bool> PassesAsync(RuleCondition condition, Dictionary<string, object?> data, Content? existing = null)
    {
        var type = await TypeAsync(host, Rule("r", condition));
        return (await RunAsync(host, type, data, existing: existing)).Passed;
    }

    private static RuleCondition Each(string field, bool negate, params RuleCondition[] inner) =>
        new() { Kind = "each", Field = field, Each = [.. inner], Negate = negate };

    private static List<Dictionary<string, object?>> Lines(params int[] qty) =>
        qty.Select(q => new Dictionary<string, object?> { ["Qty"] = q }).ToList();

    [Fact]
    public async Task Length_compares_the_text_length()
    {
        var atLeastTwo = Cond("Name", "gte", "2", kind: "length");
        (await PassesAsync(atLeastTwo, Data(("Name", "Jo")))).Should().BeTrue();
        (await PassesAsync(atLeastTwo, Data(("Name", "J")))).Should().BeFalse();
        (await PassesAsync(atLeastTwo, Data())).Should().BeFalse();
        (await PassesAsync(Cond("Name", "lte", "5", kind: "length"), Data(("Name", "Josephine")))).Should().BeFalse();
    }

    [Fact]
    public async Task Pattern_passes_a_match_and_fails_a_miss()
    {
        var mobile = Cond("Phone", "eq", @"^09\d{9}$", kind: "pattern");
        (await PassesAsync(mobile, Data(("Phone", "09171234567")))).Should().BeTrue();
        (await PassesAsync(mobile, Data(("Phone", "0917-123")))).Should().BeFalse();
        (await PassesAsync(Cond("Phone", "eq", @"^09\d{9}$", kind: "pattern", negate: true), Data(("Phone", "0917-123")))).Should().BeTrue();
    }

    [Fact]
    public async Task Pattern_holds_for_an_empty_value()
    {
        var mobile = Cond("Phone", "eq", @"^09\d{9}$", kind: "pattern");
        (await PassesAsync(mobile, Data())).Should().BeTrue();
        (await PassesAsync(mobile, Data(("Phone", "")))).Should().BeTrue();
    }

    [Fact]
    public async Task Catastrophic_pattern_times_out_and_fails_the_rule()
    {
        using var scope = host.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var data = Data(("Text", new string('a', 40) + "!"));
        var ctx = new RuleContext("t", null, data, null, null, null, null, Guid.NewGuid(), session);
        var scan = new RuleScan(ctx, [], new(), regexTimeout: TimeSpan.FromMilliseconds(50));

        var watch = Stopwatch.StartNew();
        var held = await scan.HoldsAsync(Cond("Text", "eq", "^(a+)+$", kind: "pattern"), TestContext.Current.CancellationToken);

        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
        held.Passed.Should().BeFalse();
        held.Error.Should().Contain("timed out");
    }

    [Fact]
    public async Task An_invalid_pattern_fails_the_rule()
    {
        var type = await TypeAsync(host, Rule("r", Cond("Text", "eq", "(", kind: "pattern")));

        var result = await RunAsync(host, type, Data(("Text", "x")));
        result.Passed.Should().BeFalse();
        result.Message.Should().Contain("not a valid regex");
    }

    [Fact]
    public async Task Each_requires_every_item_to_pass()
    {
        var positive = Each("Lines", false, Cond("Qty", "gt", "0"));
        (await PassesAsync(positive, Data(("Lines", Lines(1, 2))))).Should().BeTrue();
        (await PassesAsync(positive, Data(("Lines", Lines(1, 0))))).Should().BeFalse();
        (await PassesAsync(positive, Data(("Lines", Lines())))).Should().BeTrue();
        (await PassesAsync(positive, Data())).Should().BeTrue();

        var someFail = Each("Lines", true, Cond("Qty", "gt", "0"));
        (await PassesAsync(someFail, Data(("Lines", Lines(1, 0))))).Should().BeTrue();
        (await PassesAsync(someFail, Data(("Lines", Lines(1, 2))))).Should().BeFalse();
    }

    [Fact]
    public async Task Each_reads_items_from_json()
    {
        static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();
        var positive = Each("Lines", false, Cond("Qty", "gt", "0"));

        (await PassesAsync(positive, Data(("Lines", Json("""[{"Qty":1},{"Qty":2}]"""))))).Should().BeTrue();
        (await PassesAsync(positive, Data(("Lines", Json("""[{"Qty":1},{"Qty":0}]"""))))).Should().BeFalse();
        (await PassesAsync(positive, Data(("Lines", Json("[]"))))).Should().BeTrue();
    }

    [Fact]
    public async Task Each_accepts_length_and_pattern_inside()
    {
        var rule = Each("Items", false,
            Cond("Name", "gte", "1", kind: "length"),
            Cond("Code", "eq", "^[A-Z]{3}$", kind: "pattern"));
        static Dictionary<string, object?> Item(string name, string code) => new() { ["Name"] = name, ["Code"] = code };

        (await PassesAsync(rule, Data(("Items", new List<Dictionary<string, object?>> { Item("A", "ABC") })))).Should().BeTrue();
        (await PassesAsync(rule, Data(("Items", new List<Dictionary<string, object?>> { Item("", "ABC") })))).Should().BeFalse();
        (await PassesAsync(rule, Data(("Items", new List<Dictionary<string, object?>> { Item("A", "AB1") })))).Should().BeFalse();
    }

    [Fact]
    public async Task Each_on_a_field_that_is_not_an_array_fails_with_a_config_message()
    {
        var type = await TypeAsync(host, Rule("r", Each("Qty", false, Cond("X", "eq", "1"))));

        var result = await RunAsync(host, type, Data(("Qty", 3)));
        result.Passed.Should().BeFalse();
        result.Message.Should().Be("Field 'Qty' is not an array.");
    }

    [Fact]
    public async Task ItemCount_compares_the_array_length()
    {
        var atLeastOne = Cond("Lines", "gte", "1", kind: "itemCount");
        (await PassesAsync(atLeastOne, Data(("Lines", Lines())))).Should().BeFalse();
        (await PassesAsync(atLeastOne, Data())).Should().BeFalse();
        (await PassesAsync(atLeastOne, Data(("Lines", Lines(1, 2))))).Should().BeTrue();
        (await PassesAsync(Cond("Lines", "lte", "100", kind: "itemCount"), Data(("Lines", Lines(new int[101]))))).Should().BeFalse();
    }

    [Fact]
    public async Task Changed_holds_only_when_the_field_differs_from_the_stored_value()
    {
        var changed = Cond("Soap", "eq", kind: "changed");
        var stored = new Content { Id = Guid.NewGuid(), Data = new() { ["Soap"] = "a" } };

        (await PassesAsync(changed, Data(("Soap", "a")), stored)).Should().BeFalse();
        (await PassesAsync(changed, Data(("Soap", "b")), stored)).Should().BeTrue();
        (await PassesAsync(changed, Data(("Soap", "")))).Should().BeFalse();
        (await PassesAsync(changed, Data())).Should().BeFalse();
        (await PassesAsync(changed, Data(("Soap", "x")))).Should().BeTrue();
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public async Task Changed_compares_a_list_field_by_its_items_in_order()
    {
        var changed = Cond("Lines", "eq", kind: "changed");
        var stored = new Content { Id = Guid.NewGuid(), Data = new() { ["Lines"] = Json("""[{"Sku":"a","Qty":1.50},{"Sku":"b","Qty":2}]""") } };

        var same = new List<object?>
        {
            new Dictionary<string, object?> { ["Qty"] = 1.5m, ["Sku"] = "a" },
            new Dictionary<string, object?> { ["Sku"] = "b", ["Qty"] = 2 },
        };
        var added = new List<object?>(same) { new Dictionary<string, object?> { ["Sku"] = "c", ["Qty"] = 1 } };
        var reordered = new List<object?> { same[1], same[0] };
        var exact = new List<object?>
        {
            new Dictionary<string, object?> { ["Sku"] = "a", ["Qty"] = 1.51m },
            same[1],
        };

        (await PassesAsync(changed, Data(("Lines", same)), stored)).Should().BeFalse();
        (await PassesAsync(changed, Data(("Lines", added)), stored)).Should().BeTrue();
        (await PassesAsync(changed, Data(("Lines", reordered)), stored)).Should().BeTrue();
        (await PassesAsync(changed, Data(("Lines", exact)), stored)).Should().BeTrue();
    }

    [Fact]
    public async Task Changed_compares_an_object_field_ignoring_key_order()
    {
        var changed = Cond("Vitals", "eq", kind: "changed");
        var stored = new Content { Id = Guid.NewGuid(), Data = new() { ["Vitals"] = Json("""{"Bp":"120/80","Pulse":72}""") } };

        (await PassesAsync(changed, Data(("Vitals", new Dictionary<string, object?> { ["Pulse"] = 72, ["Bp"] = "120/80" })), stored)).Should().BeFalse();
        (await PassesAsync(changed, Data(("Vitals", new Dictionary<string, object?> { ["Pulse"] = 73, ["Bp"] = "120/80" })), stored)).Should().BeTrue();
    }

    [Fact]
    public async Task Changed_on_a_list_with_no_stored_entry_holds_when_the_list_is_not_empty()
    {
        var changed = Cond("Lines", "eq", kind: "changed");

        (await PassesAsync(changed, Data(("Lines", new List<object?> { "a" })))).Should().BeTrue();
    }

    [Fact]
    public async Task If_skips_the_rule_when_a_precondition_fails()
    {
        var rule = Rule("open-only", Cond("", "in", "Open", kind: "state"));
        rule.If = [Cond("Note", "eq", kind: "changed")];
        var type = await TypeAsync(host, rule);
        var paid = new Content { Id = Guid.NewGuid(), LifecycleState = "Paid", Data = new() { ["Note"] = "n" } };

        (await RunAsync(host, type, Data(("Note", "n")), existing: paid)).Passed.Should().BeTrue();
        var refused = await RunAsync(host, type, Data(("Note", "m")), existing: paid);
        refused.Passed.Should().BeFalse();
        refused.Message.Should().Be("open-only failed");

        var broken = Rule("broken", Cond("A", "eq", "1"));
        broken.If = [Cond("A", "eq", "1", kind: "nonsense")];
        var brokenType = await TypeAsync(host, broken);
        (await RunAsync(host, brokenType, Data(("A", "1")))).Message.Should().Contain("unknown condition kind");
    }

    [Fact]
    public async Task State_kind_reads_the_current_lifecycle_state()
    {
        async Task<bool> OnTransitionAsync(RuleCondition c)
        {
            var rule = Rule("r", c);
            rule.When = RuleWhen.Transition;
            return (await RunAsync(host, await TypeAsync(host, rule), Data(), RuleWhen.Transition, "Issue", "Draft")).Passed;
        }

        (await OnTransitionAsync(Cond("", "eq", "Draft", kind: "state"))).Should().BeTrue();
        (await OnTransitionAsync(Cond("", "in", "Issued,Void", kind: "state"))).Should().BeFalse();

        var issued = new Content { Id = Guid.NewGuid(), LifecycleState = "Issued" };
        (await PassesAsync(Cond("", "ne", "Issued", kind: "state"), Data(), issued)).Should().BeFalse();
        (await PassesAsync(Cond("", "empty", kind: "state"), Data())).Should().BeTrue();
    }
}
