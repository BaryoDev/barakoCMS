using barakoCMS.Infrastructure.Connectors;
using barakoCMS.Models;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests.Features.Connectors;

/// <summary>
/// Reading named values out of a provider's response, for a Request step's outputs (#1201).
/// </summary>
/// <remarks>
/// The body is third-party data. Only the paths an operator wrote are read, the grammar has no way
/// to reach anything else, and a value that is missing, too long or a credential fails the step
/// instead of being stored as null or cut.
/// </remarks>
public class ResponseOutputTests
{
    private const string Reply =
        "{\"id\":\"r1\",\"choices\":[{\"message\":{\"content\":\"First\"}},{\"message\":{\"content\":\"Second\"}}],"
        + "\"usage\":{\"total\":42,\"cached\":false},\"note\":null}";

    private static ResponseCapture Capture(
        MissingOutputRule missing = MissingOutputRule.Fail, int max = 2000, params (string Name, string Path)[] paths) =>
        new(paths.ToDictionary(p => p.Name, p => p.Path), missing, max);

    private static readonly string[] NoSecrets = [];

    [Fact]
    public void A_value_is_read_from_a_nested_path_with_an_array_index()
    {
        var (outputs, error) = ResponseOutputs.Extract(
            Reply,
            Capture(paths: [("plan", "$.choices[1].message.content"), ("first", "choices[0].message.content")]),
            NoSecrets);

        error.Should().BeNull();
        outputs.Should().NotBeNull();
        outputs!.Should().HaveCount(2);
        outputs["plan"].Should().Be("Second", "index 1 is not the default element, so reading index 0 would fail this");
        outputs["first"].Should().Be("First", "the leading $ is optional");
    }

    [Fact]
    public void A_number_and_a_boolean_are_kept_as_their_text()
    {
        var (outputs, error) = ResponseOutputs.Extract(
            Reply, Capture(paths: [("total", "$.usage.total"), ("cached", "$.usage.cached")]), NoSecrets);

        error.Should().BeNull();
        outputs.Should().NotBeNull();
        outputs!["total"].Should().Be("42");
        outputs["cached"].Should().Be("false");
    }

    [Fact]
    public void Only_the_named_paths_are_kept_and_nothing_else_of_the_body()
    {
        var (outputs, _) = ResponseOutputs.Extract(Reply, Capture(paths: [("id", "$.id")]), NoSecrets);

        outputs.Should().NotBeNull();
        outputs!.Keys.Should().Equal("id");
        outputs.Values.Should().NotContain(v => v.Contains("First"));
    }

    [Fact]
    public void A_missing_path_fails_the_step_naming_the_output_and_the_path_when_the_definition_says_fail()
    {
        var (outputs, error) = ResponseOutputs.Extract(
            Reply, Capture(MissingOutputRule.Fail, paths: [("plan", "$.choices[5].message.content")]), NoSecrets);

        outputs.Should().BeNull("a missing value must not be stored as an empty one");
        error.Should().Contain("'plan'").And.Contain("$.choices[5].message.content");
    }

    [Fact]
    public void A_null_value_counts_as_missing()
    {
        var (outputs, error) = ResponseOutputs.Extract(Reply, Capture(paths: [("note", "$.note")]), NoSecrets);

        outputs.Should().BeNull();
        error.Should().Contain("'note'");
    }

    [Fact]
    public void A_missing_path_gives_an_empty_output_when_the_definition_says_empty()
    {
        var (outputs, error) = ResponseOutputs.Extract(
            Reply,
            Capture(MissingOutputRule.Empty, paths: [("plan", "$.nope"), ("id", "$.id"), ("note", "$.note")]),
            NoSecrets);

        error.Should().BeNull();
        outputs.Should().NotBeNull();
        outputs!.Should().HaveCount(3);
        outputs["plan"].Should().BeEmpty();
        outputs["note"].Should().BeEmpty();
        outputs["id"].Should().Be("r1", "a value that is there is still read when another is missing");
    }

    [Theory]
    [InlineData(MissingOutputRule.Fail)]
    [InlineData(MissingOutputRule.Empty)]
    public void A_body_that_is_not_json_fails_the_step_under_either_rule(MissingOutputRule rule)
    {
        var (outputs, error) = ResponseOutputs.Extract(
            "<html>gateway</html>", Capture(rule, paths: [("id", "$.id")]), NoSecrets);

        outputs.Should().BeNull();
        error.Should().Contain("not JSON");
    }

    [Fact]
    public void A_value_longer_than_the_cap_fails_the_step_naming_the_cap_and_is_not_cut()
    {
        var body = "{\"text\":\"" + new string('x', 51) + "\",\"short\":\"ok\"}";

        var (outputs, error) = ResponseOutputs.Extract(
            body, Capture(max: 50, paths: [("text", "$.text"), ("short", "$.short")]), NoSecrets);

        outputs.Should().BeNull("a value cut at the cap would be stored as if it were the whole of it");
        error.Should().Contain("'text'").And.Contain("50");
    }

    [Fact]
    public void A_value_exactly_at_the_cap_is_kept()
    {
        var body = "{\"text\":\"" + new string('x', 50) + "\"}";

        var (outputs, error) = ResponseOutputs.Extract(body, Capture(max: 50, paths: [("text", "$.text")]), NoSecrets);

        error.Should().BeNull();
        outputs.Should().NotBeNull();
        outputs!["text"].Should().HaveLength(50);
    }

    [Fact]
    public void A_value_holding_a_credential_that_was_sent_fails_the_step_and_does_not_quote_it()
    {
        var body = "{\"echo\":\"you sent Bearer sk-live-12345\"}";

        var (outputs, error) = ResponseOutputs.Extract(
            body, Capture(paths: [("echo", "$.echo")]), ["sk-live-12345"]);

        outputs.Should().BeNull();
        error.Should().Contain("'echo'").And.NotContain("sk-live-12345");
    }

    [Fact]
    public void A_value_that_looks_like_a_placeholder_is_kept_as_that_text()
    {
        var body = "{\"a\":\"{{createdBy.email}}\",\"b\":\"{{actions.0.token}}\"}";

        var (outputs, error) = ResponseOutputs.Extract(
            body, Capture(paths: [("a", "$.a"), ("b", "$.b")]), NoSecrets);

        error.Should().BeNull();
        outputs.Should().NotBeNull();
        outputs!["a"].Should().Be("{{createdBy.email}}");
        outputs["b"].Should().Be("{{actions.0.token}}");
    }

    [Theory]
    [InlineData("$..a")]
    [InlineData("$.a[*]")]
    [InlineData("$['a']")]
    [InlineData("$.a[-1]")]
    [InlineData("$.a[0:2]")]
    [InlineData("$.a[?(@.b)]")]
    [InlineData("$.a.")]
    [InlineData("$.")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("$.a b")]
    [InlineData("$.a/../b")]
    [InlineData("$.a[99999]")]
    [InlineData("$.a[1][")]
    public void A_path_that_could_reach_more_than_one_value_or_is_not_a_plain_path_is_refused(string path)
    {
        OutputPath.TryParse(path, out var segments, out var error).Should().BeFalse();
        segments.Should().BeEmpty();
        error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void A_path_that_is_too_long_or_too_deep_is_refused()
    {
        OutputPath.TryParse("$." + new string('a', OutputPath.MaxLength), out _, out var tooLong).Should().BeFalse();
        tooLong.Should().NotBeNullOrEmpty();

        var deep = "$" + string.Concat(Enumerable.Repeat(".a", OutputPath.MaxSegments + 1));
        OutputPath.TryParse(deep, out _, out var deepError).Should().BeFalse();
        deepError.Should().NotBeNullOrEmpty();

        var atTheLimit = "$" + string.Concat(Enumerable.Repeat(".a", OutputPath.MaxSegments));
        OutputPath.TryParse(atTheLimit, out var segments, out _).Should().BeTrue();
        segments.Should().HaveCount(OutputPath.MaxSegments);
    }

    [Fact]
    public void A_path_parses_into_names_and_indexes_in_order()
    {
        OutputPath.TryParse("$.choices[0].message.content", out var segments, out var error).Should().BeTrue(error);

        segments.Should().HaveCount(4);
        segments[0].Name.Should().Be("choices");
        segments[1].Index.Should().Be(0);
        segments[2].Name.Should().Be("message");
        segments[3].Name.Should().Be("content");
    }

    [Fact]
    public void A_path_that_runs_past_a_value_that_is_not_an_object_or_array_is_missing()
    {
        var (outputs, error) = ResponseOutputs.Extract(
            Reply, Capture(paths: [("x", "$.id.deeper"), ("y", "$.usage[0]")]), NoSecrets);

        outputs.Should().BeNull();
        error.Should().Contain("'x'");
    }
}
