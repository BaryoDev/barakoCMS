using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Rules;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using static BarakoCMS.Tests.Rules.RuleKit;
using static BarakoCMS.Tests.Rules.RuleRows;

namespace BarakoCMS.Tests.Rules;

[Collection("Integration1")]
public class RuleDataKindsTests(IntegrationTestFixture host)
{
    private sealed class FixedLookup(string name, RuleLookupValue value) : IRuleLookup
    {
        public string Name => name;
        public Task<RuleLookupValue> ResolveAsync(IReadOnlyDictionary<string, string> args, IDocumentSession session, CancellationToken ct) =>
            Task.FromResult(value);
    }

    private static RuleCondition Unique(string field, params string[] normalise) =>
        new() { Kind = "unique", Field = field, Normalise = [.. normalise] };

    private static string Other() => $"oth{Guid.NewGuid():n}"[..20];

    [Fact]
    public async Task Unique_with_normalise_catches_formatting_differences()
    {
        var type = await TypeAsync(host, Rule("phone", Unique("Phone", "digits")));
        await StoreAsync(host, type, new() { ["Phone"] = "0917 123 4567" });

        (await RunAsync(host, type, Data(("Phone", "09171234567")))).Passed.Should().BeFalse();
        (await RunAsync(host, type, Data(("Phone", "+639171234567")))).Passed.Should().BeTrue();
    }

    [Fact]
    public async Task Unique_ignores_the_entry_being_edited()
    {
        var type = await TypeAsync(host, Rule("phone", Unique("Phone", "digits")));
        var id = await StoreAsync(host, type, new() { ["Phone"] = "09171234567" });
        var existing = new Content { Id = id, ContentType = type, Data = new() { ["Phone"] = "09171234567" } };

        (await RunAsync(host, type, Data(("Phone", "09171234567")), existing: existing)).Passed.Should().BeTrue();
    }

    [Fact]
    public async Task Unique_normalise_trim_lower()
    {
        var type = await TypeAsync(host, Rule("code", Unique("Code", "trim", "lower")));
        await StoreAsync(host, type, new() { ["Code"] = "abc" });

        (await RunAsync(host, type, Data(("Code", " ABC ")))).Passed.Should().BeFalse();
        (await RunAsync(host, type, Data(("Code", "abd")))).Passed.Should().BeTrue();
    }

    [Fact]
    public async Task Unique_reads_a_stored_key_whatever_its_case()
    {
        var type = await TypeAsync(host, Rule("phone", Unique("Phone", "digits")));
        await StoreAsync(host, type, new() { ["phone"] = "0917 123 4567" });

        (await RunAsync(host, type, Data(("Phone", "09171234567")))).Passed.Should().BeFalse();
    }

    [Fact]
    public async Task Unique_value_that_normalises_to_nothing_clashes_with_nothing()
    {
        var type = await TypeAsync(host, Rule("phone", Unique("Phone", "digits")));
        await StoreAsync(host, type, new() { ["Phone"] = "none" });

        (await RunAsync(host, type, Data(("Phone", "n/a")))).Passed.Should().BeTrue();
    }

    [Fact]
    public async Task Where_and_state_match_ignoring_case_like_eq()
    {
        var orders = Other();
        var related = new RuleCondition { Kind = "related", ContentType = orders, Where = "code={{OrderCode}}", State = "open" };
        var type = await TypeAsync(host, Rule("order", related));
        await StoreAsync(host, orders, new() { ["Code"] = "V1" }, "Open");

        (await RunAsync(host, type, Data(("OrderCode", "v1")))).Passed.Should().BeTrue();
    }

    [Fact]
    public async Task Related_requires_a_matching_entry_in_a_state()
    {
        var orders = Other();
        var related = new RuleCondition { Kind = "related", ContentType = orders, Where = "Code={{OrderCode}}", State = "Open" };
        var type = await TypeAsync(host, Rule("order", related));
        await StoreAsync(host, orders, new() { ["Code"] = "V1" }, "Open");
        await StoreAsync(host, orders, new() { ["Code"] = "V2" }, "Closed");

        (await RunAsync(host, type, Data(("OrderCode", "V1")))).Passed.Should().BeTrue();
        (await RunAsync(host, type, Data(("OrderCode", "V2")))).Passed.Should().BeFalse();
        (await RunAsync(host, type, Data(("OrderCode", "V3")))).Passed.Should().BeFalse();
    }

    [Fact]
    public async Task Count_and_sum_compare_against_a_limit()
    {
        var payments = Other();
        foreach (var amount in new[] { 100, 200, 300 })
            await StoreAsync(host, payments, new() { ["Receipt"] = "R1", ["Amount"] = amount });
        await StoreAsync(host, payments, new() { ["Receipt"] = "R2", ["Amount"] = 999 });

        RuleCondition Sum(string op) => new() { Kind = "sum", ContentType = payments, Where = "Receipt={{Receipt}}", Sum = "Amount", Op = op, Value = "600" };
        var data = Data(("Receipt", "R1"));

        (await RunAsync(host, await TypeAsync(host, Rule("lte", Sum("lte"))), data)).Passed.Should().BeTrue();
        (await RunAsync(host, await TypeAsync(host, Rule("lt", Sum("lt"))), data)).Passed.Should().BeFalse();
        var count = new RuleCondition { Kind = "count", ContentType = payments, Where = "Receipt=R1", Op = "eq", Value = "3" };
        (await RunAsync(host, await TypeAsync(host, Rule("count", count)), data)).Passed.Should().BeTrue();
    }

    [Fact]
    public async Task A_field_name_that_could_carry_SQL_is_refused()
    {
        var target = Other();
        await StoreAsync(host, target, new() { ["A"] = "1" });
        var type = await TypeAsync(host, Rule("sql", new RuleCondition { Kind = "related", ContentType = target, Where = "a'b=1" }));

        var result = await RunAsync(host, type, Data());
        result.Passed.Should().BeFalse();
        result.Message.Should().Be("Field 'a'b' is not a valid field name.");

        using var scope = host.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        (await session.Query<Content>().CountAsync(x => x.ContentType == target, TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [Fact]
    public async Task Scan_over_the_cap_fails_with_the_message()
    {
        var rows = Other();
        for (var i = 0; i < 3; i++) await StoreAsync(host, rows, new() { ["N"] = i });

        using var scope = host.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var ctx = new RuleContext("t", null, Data(), null, null, null, null, Guid.NewGuid(), session);
        var scan = new RuleScan(ctx, [], new(), cap: 2);

        var held = await scan.HoldsAsync(new RuleCondition { Kind = "count", ContentType = rows, Op = "gte", Value = "0" }, TestContext.Current.CancellationToken);
        held.Error.Should().Be("Rule scan is too large (over 2 entries).");
    }

    [Fact]
    public async Task Lookup_value_feeds_set()
    {
        var price = new RuleCondition { Kind = "lookup", Lookup = "price.lookup", Op = "notEmpty", Args = new() { ["Item"] = "{{Item}}" } };
        var rule = Rule("price", price);
        rule.Set["Total"] = "{{lookup.price.lookup.number}}";
        var type = await TypeAsync(host, rule);

        var data = Data(("Item", "X"));
        var found = await RunAsync(host, type, data, lookups: [new FixedLookup("price.lookup", new(true, null, 250))]);
        found.Passed.Should().BeTrue();
        data["Total"].Should().Be("250");

        var mustExist = new RuleCondition { Kind = "lookup", Lookup = "price.lookup", Op = "empty", Negate = true };
        var missingType = await TypeAsync(host, Rule("must", mustExist));
        (await RunAsync(host, missingType, Data(), lookups: [new FixedLookup("price.lookup", new(false, null, null))]))
            .Passed.Should().BeFalse();

        var unresolved = Rule("unresolved");
        unresolved.Set["Total"] = "{{lookup.price.lookup.number}}";
        var unresolvedType = await TypeAsync(host, unresolved);
        (await RunAsync(host, unresolvedType, Data())).Message.Should().Be(
            "Rule 'unresolved' could not fill 'Total': lookup.price.lookup.number has no value.");
    }

    [Fact]
    public async Task Unknown_lookup_name_fails_with_the_registration_message()
    {
        var type = await TypeAsync(host, Rule("l", new RuleCondition { Kind = "lookup", Lookup = "nowhere", Op = "notEmpty" }));

        (await RunAsync(host, type, Data())).Message.Should().Be("No rule lookup named 'nowhere' is registered.");
    }

    [Fact]
    public async Task notRequester_reports_that_Approvals_provides_it()
    {
        var type = await TypeAsync(host, Rule("n", new RuleCondition { Kind = "notRequester" }));

        (await RunAsync(host, type, Data())).Message.Should().Be("Condition kind 'notRequester' is provided by the Approvals module.");
    }
}
