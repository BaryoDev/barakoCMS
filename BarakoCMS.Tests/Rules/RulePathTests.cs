using System.Text.Json;
using barakoCMS.Infrastructure.Rules;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using static BarakoCMS.Tests.Rules.RuleKit;

namespace BarakoCMS.Tests.Rules;

[Collection("Integration1")]
public class RulePathTests(IntegrationTestFixture host)
{
    private static readonly object[] Results = [new { Version = 1, SignedBy = "" }, new { Version = 2, SignedBy = "Ana" }];

    private async Task<bool> PassesAsync(RuleCondition condition, Dictionary<string, object?> data)
    {
        var type = await TypeAsync(host, Rule("r", condition));
        return (await RunAsync(host, type, data)).Passed;
    }

    private async Task ThreeAnswersAsync(object? results)
    {
        var data = Data(("Results", results));
        (await PassesAsync(Cond("Results[last].SignedBy", "notEmpty"), data)).Should().BeTrue();
        (await PassesAsync(Cond("Results[0].SignedBy", "notEmpty"), data)).Should().BeFalse();
        (await PassesAsync(Cond("Results[Version=2].SignedBy", "eq", "Ana"), data)).Should().BeTrue();
    }

    [Fact]
    public async Task Field_reads_a_path_into_objects_and_arrays()
    {
        await ThreeAnswersAsync(Results);
        (await PassesAsync(Cond("Results[last].SignedBy", "notEmpty"), Data())).Should().BeFalse();
    }

    [Fact]
    public async Task Paths_read_json_elements_and_json_text_alike()
    {
        await ThreeAnswersAsync(JsonSerializer.SerializeToElement(Results));
        await ThreeAnswersAsync(JsonSerializer.Serialize(Results));
    }

    [Fact]
    public async Task Paths_read_an_entry_stored_through_Marten()
    {
        var id = Guid.NewGuid();
        using (var scope = host.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new Content { Id = id, ContentType = "pathprobe", Data = new() { ["Results"] = Results } });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var read = host.Services.CreateScope();
        var loaded = await read.ServiceProvider.GetRequiredService<IDocumentSession>().LoadAsync<Content>(id, TestContext.Current.CancellationToken);
        RulePath.Text(loaded!.Data.ToDictionary(p => p.Key, p => (object?)p.Value), "Results[last].SignedBy", null).Should().Be("Ana");
    }

    [Fact]
    public void Keys_reads_the_names_of_an_object_map()
    {
        var rates = new Dictionary<string, object?> { ["LAB01"] = new { Rate = 80 }, ["CONS"] = new { Rate = 0 } };

        RulePath.Values(Data(("Rates", rates)), "Rates.$keys", null).Select(RulePath.TextOf).Should().Equal("LAB01", "CONS");
        RulePath.Values(Data(("Rates", JsonSerializer.Serialize(rates))), "Rates.$keys", null).Select(RulePath.TextOf)
            .Should().Equal("LAB01", "CONS");
    }

    [Fact]
    public void A_path_through_an_array_reaches_every_item()
    {
        var lines = new object[] { new { Amount = 10, Covered = true }, new { Amount = 20, Covered = false }, new { Amount = 30, Covered = true } };
        var data = Data(("Lines", lines));

        RulePath.Values(data, "Lines.Amount", null).Select(RulePath.TextOf).Should().Equal("10", "20", "30");
        RulePath.Values(data, "Lines[Covered=true].Amount", null).Select(RulePath.TextOf).Should().Equal("10", "30");
    }

    [Fact]
    public async Task Value_compares_with_another_field()
    {
        var tendered = Cond("CashTendered", "gte", "{{CashAmount}}");
        (await PassesAsync(tendered, Data(("CashTendered", 500), ("CashAmount", 450)))).Should().BeTrue();
        (await PassesAsync(tendered, Data(("CashTendered", 400), ("CashAmount", 450)))).Should().BeFalse();
    }

    [Fact]
    public async Task Parent_reads_the_outer_entry_inside_each()
    {
        var each = new RuleCondition { Kind = "each", Field = "Lines", Each = [Cond("Qty", "lte", "{{parent.Max}}")] };
        static List<Dictionary<string, object?>> Lines(params int[] qty) => qty.Select(q => new Dictionary<string, object?> { ["Qty"] = q }).ToList();

        (await PassesAsync(each, Data(("Max", 3), ("Lines", Lines(2, 2))))).Should().BeTrue();
        (await PassesAsync(each, Data(("Max", 3), ("Lines", Lines(2, 4))))).Should().BeFalse();
    }

    [Fact]
    public async Task A_condition_guard_skips_only_that_condition()
    {
        var rate = Cond("Rate", "notEmpty");
        rate.If = [Cond("Covered", "eq", "true")];
        (await PassesAsync(rate, Data(("Covered", false), ("Rate", "")))).Should().BeTrue();
        (await PassesAsync(rate, Data(("Covered", true), ("Rate", "")))).Should().BeFalse();

        var broken = Cond("Rate", "notEmpty");
        broken.If = [Cond("Covered", "eq", "true", kind: "nonsense")];
        var type = await TypeAsync(host, Rule("r", broken));
        (await RunAsync(host, type, Data(("Rate", "1")))).Message.Should().Contain("unknown condition kind");
    }

    [Fact]
    public async Task Messages_are_templates_and_a_condition_message_wins()
    {
        var rule = Rule("left", Cond("Left", "gt", "5"));
        rule.Message = "Only {{Left}} left.";
        (await RunAsync(host, await TypeAsync(host, rule), Data(("Left", 2)))).Message.Should().Be("Only 2 left.");

        var own = Cond("Qty", "lte", "1");
        own.Message = "Too many.";
        var second = Rule("left", Cond("Left", "gt", "1"), own);
        second.Message = "Only {{Left}} left.";
        (await RunAsync(host, await TypeAsync(host, second), Data(("Left", 2), ("Qty", 3)))).Message.Should().Be("Too many.");
    }
}
