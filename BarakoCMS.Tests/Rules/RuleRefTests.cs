using barakoCMS.Infrastructure.Envelope;
using barakoCMS.Models;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using FluentAssertions;
using static BarakoCMS.Tests.Rules.RuleKit;
using static BarakoCMS.Tests.Rules.RuleRows;

namespace BarakoCMS.Tests.Rules;

[Collection("Integration1")]
public class RuleRefTests(IntegrationTestFixture host)
{
    private static string Name(string prefix) => $"{prefix}{Guid.NewGuid():n}"[..20];

    private static RuleCondition On(string kind, string? field = null, string op = "eq", string? value = null, string? @ref = "{{OrderId}}") =>
        new() { Kind = kind, Ref = @ref, Field = field, Op = op, Value = value };

    private static List<object?> List(params object?[] items) => [.. items];

    private static Dictionary<string, object?> Map(params (string Key, object? Value)[] pairs) => Data(pairs);

    private async Task<bool> PassesAsync(RuleCondition c, Dictionary<string, object?> data)
    {
        var type = await TypeAsync(host, Rule("r", c));
        return (await RunAsync(host, type, data)).Passed;
    }

    private async Task<Guid> OrderAsync(string type, object? lines = null)
    {
        var data = new Dictionary<string, object> { ["MedCertDays"] = 2 };
        if (lines is not null) data["Lines"] = lines;
        return await StoreAsync(host, type, data, "WithBalance");
    }

    [Fact]
    public async Task Ref_reads_one_entry_by_id()
    {
        var v = await OrderAsync(Name("order"));
        var rule = On("field", "MedCertDays", "gt", "0");

        (await PassesAsync(rule, Data(("OrderId", v.ToString())))).Should().BeTrue();
        (await PassesAsync(rule, Data(("OrderId", Guid.NewGuid().ToString())))).Should().BeFalse();
        (await PassesAsync(rule, Data(("OrderId", "not-a-guid")))).Should().BeFalse();
    }

    [Theory]
    [InlineData("{{{0}}}")]
    [InlineData(" {0} ")]
    [InlineData("({0})")]
    [InlineData("N")]
    public async Task Ref_reads_only_the_plain_id_form(string shape)
    {
        var v = await OrderAsync(Name("order"));
        var id = shape == "N" ? v.ToString("N") : string.Format(shape, v);
        var rule = On("field", "MedCertDays", "gt", "0");

        (await PassesAsync(rule, Data(("OrderId", id)))).Should().BeFalse();
        (await PassesAsync(rule, Data(("OrderId", v.ToString().ToUpperInvariant())))).Should().BeTrue();
    }

    [Fact]
    public async Task Ref_does_not_read_an_entry_of_another_branch()
    {
        var (mine, other) = (Guid.NewGuid(), Guid.NewGuid());
        var v = await StoreAsync(host, Name("order"), new() { ["MedCertDays"] = 2 }, "WithBalance", mine);
        var type = await TypeAsync(host, Rule("r", On("field", "MedCertDays", "gt", "0")));
        var envelope = new EventEnvelopeContext(host.Services.GetRequiredService<IDocumentStore>());
        async Task<bool> AsAsync(Guid? branch)
        {
            using var _ = envelope.Use(branch);
            return (await RunAsync(host, type, Data(("OrderId", v.ToString())))).Passed;
        }

        (await AsAsync(mine)).Should().BeTrue();
        (await AsAsync(other)).Should().BeFalse();
        (await AsAsync(null)).Should().BeFalse();
    }

    [Fact]
    public async Task Ref_with_a_content_type_ignores_another_type()
    {
        var v = await OrderAsync(Name("order"));
        var rule = On("field", "MedCertDays", "gt", "0");
        rule.ContentType = Name("customer");

        (await PassesAsync(rule, Data(("OrderId", v.ToString())))).Should().BeFalse();
    }

    [Fact]
    public async Task State_and_related_read_through_ref()
    {
        var data = Data(("OrderId", (await OrderAsync(Name("order"))).ToString()));
        var related = On("related");

        (await PassesAsync(On("state", value: "WithBalance"), data)).Should().BeTrue();
        related.State = "Paid,Closed";
        (await PassesAsync(related, data)).Should().BeFalse();
        related.State = "WithBalance,Paid";
        (await PassesAsync(related, data)).Should().BeTrue();
    }

    [Fact]
    public async Task Sum_reads_a_list_on_the_ref_entry()
    {
        var v = await OrderAsync(Name("order"), new[] { new { Amount = 300, CoveredByHmo = true }, new { Amount = 200, CoveredByHmo = false } });
        var data = Data(("OrderId", v.ToString()));
        var sum = On("sum", value: "500");
        sum.Sum = "Lines.Amount";

        (await PassesAsync(sum, data)).Should().BeTrue();
        sum.Sum = "Lines[CoveredByHmo=true].Amount";
        sum.Value = "300";
        (await PassesAsync(sum, data)).Should().BeTrue();
    }

    [Fact]
    public async Task Sum_reads_a_list_across_rows()
    {
        var delivery = Name("delivery");
        for (var i = 0; i < 2; i++)
            await StoreAsync(host, delivery, new() { ["Batch"] = "B1", ["Lines"] = new[] { new { Amount = 10 }, new { Amount = 5 } } });
        var sum = new RuleCondition { Kind = "sum", ContentType = delivery, Where = "Batch=B1", Sum = "Lines.Amount", Op = "eq", Value = "30" };

        (await PassesAsync(sum, Data())).Should().BeTrue();
    }

    [Fact]
    public async Task State_takes_a_comma_list()
    {
        var shipment = Name("shipment");
        var v = Guid.NewGuid().ToString();
        foreach (var state in new[] { "Released", "Amended", "Draft" })
            await StoreAsync(host, shipment, new() { ["OrderId"] = v }, state);
        var count = new RuleCondition { Kind = "count", ContentType = shipment, Where = "OrderId={{OrderId}}", State = "Released,Amended", Op = "eq", Value = "2" };

        (await PassesAsync(count, Data(("OrderId", v)))).Should().BeTrue();
    }

    [Fact]
    public async Task Member_checks_values_against_rows()
    {
        var overrides = Name("override");
        var (approved, pending) = (Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
        await StoreAsync(host, overrides, new() { ["OrderId"] = approved, ["Keys"] = new[] { "lab:CBC", "med-cert" } }, "Approved");
        await StoreAsync(host, overrides, new() { ["OrderId"] = pending, ["Keys"] = new[] { "lab:CBC", "med-cert" } }, "PendingApproval");
        var member = new RuleCondition
        {
            Kind = "member", Field = "Items.Key", Of = "Keys", ContentType = overrides, Where = "OrderId={{OrderId}}", State = "Approved",
        };
        Dictionary<string, object?> Order(string id, params string[] keys) =>
            Data(("OrderId", id), ("Items", keys.Select(k => (object?)Map(("Key", k))).ToList()));

        (await PassesAsync(member, Order(approved, "lab:CBC"))).Should().BeTrue();
        (await PassesAsync(member, Order(approved, "lab:CBC", "lab:FBS"))).Should().BeFalse();
        (await PassesAsync(member, Order(pending, "lab:CBC"))).Should().BeFalse();
        (await PassesAsync(member, Order(approved))).Should().BeTrue();
    }

    [Fact]
    public async Task Member_takes_a_template_per_row_and_also()
    {
        var shipment = Name("shipment");
        var v = Guid.NewGuid().ToString();
        await StoreAsync(host, shipment, new() { ["OrderId"] = v, ["TestCode"] = "CBC" }, "Released");
        var member = new RuleCondition
        {
            Kind = "member", Field = "Keys", Of = "lab:{{TestCode}}", ContentType = shipment, Where = "OrderId={{OrderId}}",
            State = "Released", Also = "med-cert",
        };

        (await PassesAsync(member, Data(("OrderId", v), ("Keys", List("lab:CBC", "med-cert"))))).Should().BeTrue();
        (await PassesAsync(member, Data(("OrderId", v), ("Keys", List("lab:XRAY"))))).Should().BeFalse();
    }

    [Fact]
    public async Task Member_of_this_entry_and_a_value()
    {
        var member = new RuleCondition { Kind = "member", Value = "med-cert", Of = "Keys" };

        (await PassesAsync(member, Data(("Keys", List("med-cert"))))).Should().BeTrue();
        (await PassesAsync(member, Data(("Keys", List("lab:CBC"))))).Should().BeFalse();
        member.Negate = true;
        (await PassesAsync(member, Data(("Keys", List("med-cert"))))).Should().BeFalse();
        (await PassesAsync(member, Data(("Keys", List("lab:CBC"))))).Should().BeTrue();
    }

    [Fact]
    public async Task Object_maps_count_and_iterate()
    {
        (await PassesAsync(Cond("Values", "eq", "2", kind: "itemCount"), Data(("Values", Map(("a", 1), ("b", 2)))))).Should().BeTrue();

        var rates = Map(("LAB01", Map(("Rate", 80), ("Covered", true))), ("X1", Map(("Rate", ""), ("Covered", true))));
        const string ratesJson = """{"LAB01": {"Rate": 80, "Covered": true}, "X1": {"Rate": "", "Covered": true}}""";
        var rateSet = new RuleCondition { Kind = "each", Field = "Rates", Each = [Cond("Rate", "notEmpty")] };
        var keyShape = new RuleCondition { Kind = "each", Field = "Rates", Each = [Cond("$key", "", "^[A-Z0-9]+$", kind: "pattern")] };

        foreach (var value in new object[] { rates, ratesJson })
        {
            (await PassesAsync(rateSet, Data(("Rates", value)))).Should().BeFalse();
            (await PassesAsync(keyShape, Data(("Rates", value)))).Should().BeTrue();
        }
        (await PassesAsync(Cond("Values", "eq", "2", kind: "itemCount"), Data(("Values", """{"a": 1, "b": 2}""")))).Should().BeTrue();
    }

    [Fact]
    public async Task Covers_needs_every_value_from_the_rows()
    {
        var catalog = Name("labcatalog");
        await StoreAsync(host, catalog, new() { ["Code"] = "CBC", ["Parameters"] = new[] { new { Code = "WBC" }, new { Code = "RBC" } } });
        var covers = new RuleCondition
        {
            Kind = "covers", Field = "Results[last].Values.$keys", ContentType = catalog, Where = "Code={{TestCode}}", Of = "Parameters.Code",
        };
        var full = Map(("Values", Map(("WBC", 5), ("RBC", 4))));
        var partial = Map(("Values", Map(("WBC", 5))));

        (await PassesAsync(covers, Data(("TestCode", "CBC"), ("Results", List(full))))).Should().BeTrue();
        (await PassesAsync(covers, Data(("TestCode", "CBC"), ("Results", List(full, partial))))).Should().BeFalse();
        (await PassesAsync(covers, Data(("TestCode", "XRAY"), ("Results", List(partial))))).Should().BeTrue();
        covers.Also = "HGB";
        (await PassesAsync(covers, Data(("TestCode", "CBC"), ("Results", List(full))))).Should().BeFalse();
    }

    [Fact]
    public async Task Distinct_finds_repeats_through_a_path()
    {
        var payers = new RuleCondition { Kind = "distinct", Field = "PriceList.PayerKey", Normalise = ["trim", "lower"] };
        Dictionary<string, object?> Prices(params string[] keys) =>
            Data(("PriceList", keys.Select(k => (object?)Map(("PayerKey", k))).ToList()));

        (await PassesAsync(payers, Prices("cash", "maxicare"))).Should().BeTrue();
        (await PassesAsync(payers, Prices("cash", " Cash"))).Should().BeFalse();
        payers.Normalise = null;
        (await PassesAsync(payers, Prices("cash", " Cash"))).Should().BeTrue();

        var codes = new RuleCondition { Kind = "distinct", Field = "Codes" };
        (await PassesAsync(codes, Data(("Codes", List("A", "B", "A"))))).Should().BeFalse();
        (await PassesAsync(codes, Data(("Codes", List("A", "B"))))).Should().BeTrue();
        (await PassesAsync(codes, Data(("Codes", List())))).Should().BeTrue();
    }

    [Fact]
    public async Task Each_runs_over_plain_values_and_answers_with_the_inner_message()
    {
        var catalog = Name("labcatalog");
        await StoreAsync(host, catalog, new() { ["Code"] = "CBC" });
        await StoreAsync(host, catalog, new() { ["Code"] = "FBS" });
        var known = new RuleCondition
        {
            Kind = "member", Value = "{{$value}}", ContentType = catalog, Of = "Code", Message = "{{$value}} is not a test in the catalog.",
        };
        var package = await TypeAsync(host, new RuleDefinition
        {
            Name = "package", All = [new() { Kind = "each", Field = "Codes", Each = [known] }], Message = "bad package",
        });

        (await RunAsync(host, package, Data(("Codes", List("CBC", "FBS"))))).Passed.Should().BeTrue();
        (await RunAsync(host, package, Data(("Codes", List("CBC", "ZZZ"))))).Message.Should().Be("ZZZ is not a test in the catalog.");

        var qty = Cond("Qty", "gt", "0");
        qty.Message = "{{Name}}: quantity must be above 0.";
        var lines = await TypeAsync(host, Rule("lines", new RuleCondition { Kind = "each", Field = "Lines", Each = [qty] }));
        var gauze = Data(("Lines", List(Map(("Name", "Gauze"), ("Qty", 0)))));
        (await RunAsync(host, lines, gauze)).Message.Should().Be("Gauze: quantity must be above 0.");
        var plain = await TypeAsync(host, Rule("plain", new RuleCondition { Kind = "each", Field = "Lines", Each = [Cond("Qty", "gt", "0")] }));
        (await RunAsync(host, plain, gauze)).Message.Should().Be("plain failed");

        (await PassesAsync(Cond("Codes", "eq", "2", kind: "itemCount"), Data(("Codes", List("CBC", "FBS"))))).Should().BeTrue();
    }

    [Fact]
    public async Task A_ref_can_come_from_a_let()
    {
        var (orders, customers) = (Name("order"), Name("customer"));
        var senior = await StoreAsync(host, customers, new() { ["SeniorPwdId"] = "SC-1" });
        var plain = await StoreAsync(host, customers, new() { ["SeniorPwdId"] = "" });
        var seniorOrder = await StoreAsync(host, orders, new() { ["CustomerId"] = senior.ToString() });
        var plainOrder = await StoreAsync(host, orders, new() { ["CustomerId"] = plain.ToString() });
        var rule = Rule("senior", On("field", "SeniorPwdId", "notEmpty", @ref: "{{let.customerId}}"));
        rule.Let = [new() { Kind = "field", Ref = "{{OrderId}}", Field = "CustomerId", As = "customerId" }];
        var type = await TypeAsync(host, rule);

        (await RunAsync(host, type, Data(("OrderId", seniorOrder.ToString())))).Passed.Should().BeTrue();
        (await RunAsync(host, type, Data(("OrderId", plainOrder.ToString())))).Passed.Should().BeFalse();
    }
}
