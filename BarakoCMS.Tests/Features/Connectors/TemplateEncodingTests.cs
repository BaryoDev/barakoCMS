using System.Text.Json;
using barakoCMS.Features.EmailTemplates;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests.Features.Connectors;

/// <summary>
/// A value is encoded once, for the place it lands, after the formula has run. One value holds a
/// quote, an angle bracket, an ampersand, a line break and a placeholder, and it is never read as a
/// template again.
/// </summary>
[Collection("Integration1")]
public class TemplateEncodingTests(IntegrationTestFixture fixture)
{
    private const string Value = "a\"b<c&d\ne{{id}}f";

    private static Content Entry(string value = Value) => new()
    {
        Id = Guid.NewGuid(), ContentType = "note", Status = ContentStatus.Published, Data = new() { ["Note"] = value },
    };

    public static TheoryData<string> HtmlForms =>
    [
        "{{data.Note}}",
        "{{data.Note | lower}}",
        "{{= data.Note}}",
        "{{= data.Note | lower}}",
    ];

    [Theory]
    [MemberData(nameof(HtmlForms))]
    public void In_an_html_body_a_value_is_html_encoded_with_its_braces_and_never_read_again(string form)
    {
        var entry = Entry();
        var rendered = new RenderedTemplate("s", EmailTemplateRenderer.Markdown($"Note: {form}"), []);

        var (_, body) = EmailTemplateRenderer.Resolve(rendered, entry, null);

        body.Should().Contain("a&quot;b&lt;c&amp;d\ne&#123;&#123;id&#125;&#125;f");
        body.Should().NotContain("<c").And.NotContain(entry.Id.ToString());
    }

    [Fact]
    public void In_a_subject_a_line_break_becomes_a_space_and_the_placeholder_is_not_read_again()
    {
        var entry = Entry();

        var subject = TemplateVariableExtractor.Resolve("Re: {{data.Note}}", entry, TemplateValueEncoding.SingleLine);

        subject.Should().Be("Re: a\"b<c&d e{{id}}f");
        subject.Should().NotContain(entry.Id.ToString());
    }

    [Theory]
    [InlineData("{{Note}}", Value)]
    [InlineData("{{Note | lower}}", Value)]
    [InlineData("{{Note | upper}}", "A\"B<C&D\nE{{ID}}F")]
    public async Task In_a_json_body_a_value_is_a_json_string_that_reads_back_as_the_value(string hole, string expected)
    {
        var subject = await SeedAsync(Value);
        var definition = RequestKit.Definition(subject, "{\"note\":\"" + hole + "\",\"fixed\":1}");

        var composed = await RequestKit.ComposeAsync(fixture, subject, definition);

        composed.Ok.Should().BeTrue(composed.Refusal);
        using var parsed = JsonDocument.Parse(composed.Body!);
        parsed.RootElement.GetProperty("note").GetString().Should().Be(expected);
        parsed.RootElement.GetProperty("fixed").GetInt32().Should().Be(1, "the value did not add or change a member");
        composed.Body.Should().NotContain(subject.EntryId.ToString());
    }

    [Fact]
    public async Task In_a_json_body_a_value_that_closes_the_string_cannot_add_a_member()
    {
        var subject = await SeedAsync("\",\"admin\":true,\"x\":\"");
        var definition = RequestKit.Definition(subject, "{\"note\":\"{{Note}}\"}");

        var composed = await RequestKit.ComposeAsync(fixture, subject, definition);

        composed.Ok.Should().BeTrue(composed.Refusal);
        using var parsed = JsonDocument.Parse(composed.Body!);
        parsed.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal("note");
    }

    [Fact]
    public async Task In_a_url_path_a_value_is_one_escaped_segment()
    {
        var subject = await SeedAsync(Value);

        var composed = await RequestKit.ComposeAsync(fixture, subject, RequestKit.Definition(subject, null, "/p/{{Note}}"));

        composed.Ok.Should().BeTrue(composed.Refusal);
        composed.Url.Should().Be("https://egress.example/p/" + Uri.EscapeDataString(Value));
        new Uri(composed.Url).Segments.Should().HaveCount(3);
        composed.Url.Should().NotContain(subject.EntryId.ToString());
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData("../admin")]
    [InlineData("x/../../admin")]
    [InlineData("?q=1#frag")]
    public async Task In_a_url_path_a_slash_a_dot_dot_or_a_query_in_a_value_does_not_change_the_path(string value)
    {
        var subject = await SeedAsync(value);

        var composed = await RequestKit.ComposeAsync(fixture, subject, RequestKit.Definition(subject, null, "/p/{{Note}}"));

        composed.Ok.Should().BeTrue(composed.Refusal);
        var url = new Uri(composed.Url);
        url.Segments.Should().HaveCount(3, "the value stays one segment of /p/");
        url.Segments[0].Should().Be("/");
        url.Segments[1].Should().Be("p/");
        url.Query.Should().BeEmpty();
        url.Fragment.Should().BeEmpty();
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    public async Task In_a_url_path_a_value_that_is_a_dot_segment_is_refused(string value)
    {
        var subject = await SeedAsync(value);

        var composed = await RequestKit.ComposeAsync(fixture, subject, RequestKit.Definition(subject, null, "/p/{{Note}}"));

        composed.Ok.Should().BeFalse("it would address /, not a segment under /p/");
        composed.Refusal.Should().Contain("dot segment");
    }

    [Fact]
    public async Task A_dot_segment_written_elsewhere_in_the_template_does_not_excuse_a_value_that_is_one()
    {
        var subject = await SeedAsync("..");

        var composed = await RequestKit.ComposeAsync(fixture, subject, RequestKit.Definition(subject, null, "/a/../b/{{Note}}"));

        composed.Ok.Should().BeFalse();
        composed.Refusal.Should().Contain("dot segment");
    }

    [Fact]
    public async Task A_template_with_its_own_dot_segment_composes_when_the_value_is_not_one()
    {
        var subject = await SeedAsync("x");

        var composed = await RequestKit.ComposeAsync(fixture, subject, RequestKit.Definition(subject, null, "/a/../b/{{Note}}"));

        composed.Ok.Should().BeTrue(composed.Refusal);
    }

    [Fact]
    public async Task A_segment_built_from_two_values_that_together_make_a_dot_segment_is_refused()
    {
        var subject = await SeedAsync(".");

        var composed = await RequestKit.ComposeAsync(fixture, subject, RequestKit.Definition(subject, null, "/p/{{Note}}{{Note}}"));

        composed.Ok.Should().BeFalse();
        composed.Refusal.Should().Contain("dot segment");
    }

    [Fact]
    public async Task A_value_that_spells_an_escaped_dot_segment_is_data_and_composes()
    {
        var subject = await SeedAsync("%2e%2e");

        var composed = await RequestKit.ComposeAsync(fixture, subject, RequestKit.Definition(subject, null, "/p/{{Note}}"));

        composed.Ok.Should().BeTrue(composed.Refusal);
        composed.Url.Should().EndWith("/p/%252e%252e");
    }

    [Fact]
    public async Task A_dot_segment_the_template_wrote_is_the_operators_own()
    {
        var subject = await SeedAsync("x");

        var composed = await RequestKit.ComposeAsync(fixture, subject, RequestKit.Definition(subject, null, "/p/q/../{{Note}}"));

        composed.Ok.Should().BeTrue(composed.Refusal);
    }

    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    [InlineData("a\r\nX-Injected: yes")]
    public async Task In_a_header_a_line_break_is_refused_and_nothing_is_composed(string value)
    {
        var subject = await SeedAsync(value);
        var definition = RequestKit.Definition(subject, null);
        definition.HeaderTemplates["X-Note"] = "{{Note}}";

        var composed = await RequestKit.ComposeAsync(fixture, subject, definition);

        composed.Ok.Should().BeFalse();
        composed.Refusal.Should().Contain("X-Note").And.Contain("control character");
        composed.Headers.Should().BeEmpty();
    }

    [Fact]
    public async Task In_a_header_the_rest_of_the_value_goes_as_written_and_the_placeholder_is_not_read_again()
    {
        var subject = await SeedAsync("a\"b<c&d{{id}}f");
        var definition = RequestKit.Definition(subject, null);
        definition.HeaderTemplates["X-Note"] = "{{Note}}";

        var composed = await RequestKit.ComposeAsync(fixture, subject, definition);

        composed.Ok.Should().BeTrue(composed.Refusal);
        composed.Headers["X-Note"].Should().Be("a\"b<c&d{{id}}f");
    }

    private Task<RequestKit.Subject> SeedAsync(string value) => RequestKit.SeedAsync(
        fixture, [new FieldDefinition { Name = "Note", Type = "string" }], new Dictionary<string, object> { ["Note"] = value });
}
