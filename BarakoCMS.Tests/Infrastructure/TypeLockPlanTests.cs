using System.Text.Json;
using barakoCMS.Infrastructure.Jobs;
using barakoCMS.Models;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests.Infrastructure;

/// <summary>
/// The lock plan of a save that writes several types: exclusive on what it writes, shared on what
/// its rollups read outside that, lowest normalized name first.
/// </summary>
public class TypeLockPlanTests
{
    private static Dictionary<string, JsonElement> Documents(params (string Name, string Json)[] documents) =>
        documents.ToDictionary(d => d.Name, d => JsonDocument.Parse(d.Json).RootElement.Clone());

    private static string Rolls(string from) =>
        "{\"fields\":[{\"name\":\"Total\",\"type\":\"int\",\"rollup\":{\"from\":\"" + from + "\",\"op\":\"count\"}}]}";

    [Fact]
    public void A_document_type_is_exclusive_and_an_outside_source_is_shared_in_one_sorted_list()
    {
        var plan = TypeLockPlan.ForDocuments(Documents(("Mid", Rolls("Zed")), ("Alpha", Rolls("Beta"))));

        plan.Should().Equal(
            new TypeLock("alpha", true),
            new TypeLock("beta", false),
            new TypeLock("mid", true),
            new TypeLock("zed", false));
    }

    [Fact]
    public void A_source_the_document_also_holds_is_only_exclusive()
    {
        var plan = TypeLockPlan.ForDocuments(Documents(("A", Rolls("B")), ("B", "{}")));

        plan.Should().Equal(new TypeLock("a", true), new TypeLock("b", true));
    }

    [Fact]
    public void Names_are_normalized_so_a_source_in_another_case_or_with_spaces_is_the_same_lock()
    {
        var plan = TypeLockPlan.ForDocuments(Documents(("Class Room", Rolls("class room")), ("Other", Rolls("  TEACHER "))));

        plan.Should().Equal(
            new TypeLock("class-room", true),
            new TypeLock("other", true),
            new TypeLock("teacher", false));
    }

    [Fact]
    public void Blank_duplicate_and_overlong_names_are_left_out_or_taken_once()
    {
        var plan = TypeLockPlan.For(
            ["A", "a", " ", "", null, new string('x', 300)],
            ["B", "b", "", null, "   ", new string('y', 300), "A"]);

        plan.Should().Equal(new TypeLock("a", true), new TypeLock("b", false));
    }

    [Fact]
    public void Property_names_are_read_without_regard_to_case()
    {
        var plan = TypeLockPlan.ForDocuments(Documents(
            ("A", "{\"Fields\":[{\"Rollup\":{\"From\":\"X\"}}]}")));

        plan.Should().Equal(new TypeLock("a", true), new TypeLock("x", false));
    }

    [Theory]
    [InlineData("{\"fields\":[{\"rollup\":\"x\"}]}")]
    [InlineData("{\"fields\":[{\"rollup\":{\"from\":7}}]}")]
    [InlineData("{\"fields\":[{\"rollup\":{\"from\":null}}]}")]
    [InlineData("{\"fields\":[{\"rollup\":null}]}")]
    [InlineData("{\"fields\":[{\"rollup\":[]}]}")]
    [InlineData("{\"fields\":\"none\"}")]
    [InlineData("{\"fields\":[1,\"two\",null]}")]
    [InlineData("{\"fields\":{\"rollup\":{\"from\":\"x\"}}}")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("null")]
    public void A_malformed_rollup_or_document_is_ignored_and_does_not_throw(string json)
    {
        var plan = TypeLockPlan.ForDocuments(Documents(("A", json)));

        plan.Should().Equal(new TypeLock("a", true));
    }

    [Fact]
    public void Type_definitions_give_the_same_plan_as_documents()
    {
        var types = new List<ContentTypeDefinition>
        {
            new() { Name = "Mid", Fields = [new FieldDefinition { Name = "Total", Rollup = new RollupDefinition { From = "Zed" } }] },
            new() { Name = "Alpha", Fields = [new FieldDefinition { Name = "Plain", Type = "string" }] },
        };

        TypeLockPlan.ForTypes(types).Should().Equal(
            new TypeLock("alpha", true),
            new TypeLock("mid", true),
            new TypeLock("zed", false));
    }
}
