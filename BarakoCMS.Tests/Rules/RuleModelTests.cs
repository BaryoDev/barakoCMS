using System.Text.Json;
using System.Text.Json.Serialization;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests.Rules;

public class RuleModelTests
{
    private const string Config = """
    {
      "Name": "Receipt",
      "Rules": [
        { "Name": "needs-order", "When": "Transition", "Transition": "Issue", "InState": "Draft",
          "All": [ { "Kind": "field", "Field": "Order", "Op": "notEmpty" },
                   { "Kind": "unique", "Field": "Number", "Normalise": ["trim", "lower"] },
                   { "Kind": "each", "Field": "Lines", "Each": [ { "Kind": "field", "Field": "Qty", "Op": "gt", "Value": "0" } ] } ],
          "Set": { "Total": "{{lookup.price.lookup.number}}" },
          "Message": "A receipt needs a order." }
      ]
    }
    """;

    private static ContentTypeDefinition Read() => JsonSerializer.Deserialize<ContentTypeDefinition>(
        Config, new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } })!;

    [Fact]
    public void Rules_read_from_content_type_config()
    {
        var rule = Read().Rules!.Single();
        rule.When.Should().Be(RuleWhen.Transition);
        rule.Transition.Should().Be("Issue");
        rule.All.Should().HaveCount(3);
        rule.All[0].Op.Should().Be("notEmpty");
        rule.All[1].Normalise.Should().Equal("trim", "lower");
        rule.All[2].Each.Should().ContainSingle().Which.Op.Should().Be("gt");
        rule.Set["Total"].Should().Be("{{lookup.price.lookup.number}}");
    }

    [Fact]
    public void Defaults_are_save_time_field_equals()
    {
        var rule = new RuleDefinition();
        rule.When.Should().Be(RuleWhen.Save);
        new RuleCondition().Kind.Should().Be("field");
        new RuleCondition().Op.Should().Be("eq");
    }

    [Fact]
    public void Create_and_merge_carry_the_rules()
    {
        var given = Read();
        ContentTypeUpsert.Create(given).Rules.Should().HaveCount(1);
        var stored = new ContentTypeDefinition { Name = "receipt" };
        ContentTypeUpsert.MergeInto(stored, given);
        stored.Rules.Should().HaveCount(1);
    }
}
