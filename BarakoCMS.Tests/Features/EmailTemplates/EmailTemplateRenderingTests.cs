using barakoCMS.Features.EmailTemplates;
using barakoCMS.Features.Workflows;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests.Features.EmailTemplates;

/// <summary>
/// A template's markdown becomes HTML that can only hold the markup markdown and the layout make,
/// with every placeholder kept for the resolver, and links that point at http, https or mailto only.
/// </summary>
public class EmailTemplateRenderingTests
{
    [Fact]
    public void Markdown_renders_and_raw_html_in_it_is_shown_as_text()
    {
        var html = EmailTemplateRenderer.Markdown("**Hello** <script>alert(1)</script>");

        html.Should().Contain("<strong>Hello</strong>");
        html.Should().Contain("&lt;script&gt;alert(1)&lt;/script&gt;");
        html.Should().NotContain("<script>");
    }

    [Fact]
    public void A_placeholder_reaches_the_resolver_as_written_inside_a_link_and_beside_underscores()
    {
        var html = EmailTemplateRenderer.Markdown("[Open]({{links.site \"/a b\"}}) and _{{data.first_name}}_");

        html.Should().Contain("<a href=\"{{links.site \"/a b\"}}\">Open</a>");
        html.Should().Contain("<em>{{data.first_name}}</em>");
    }

    [Fact]
    public void A_link_or_image_on_another_scheme_is_dropped_and_http_https_and_mailto_links_are_kept()
    {
        var finished = EmailTemplateRenderer.Finish(
            "<a href=\"javascript:alert(1)\">a</a>"
            + "<a href=\"&#106;avascript:alert(1)\">b</a>"
            + "<a href=\"/relative\">c</a>"
            + "<a href=\"https://x.example/p\">d</a>"
            + "<a href=\"mailto:a@x.example\">e</a>"
            + "<img src=\"mailto:a@x.example\" alt=\"f\">"
            + "<img src=\"http://x.example/i.png\" alt=\"g\">");

        finished.Should().Be(
            "<a>a</a><a>b</a><a>c</a>"
            + "<a href=\"https://x.example/p\">d</a>"
            + "<a href=\"mailto:a@x.example\">e</a>"
            + "<img alt=\"f\">"
            + "<img src=\"http://x.example/i.png\" alt=\"g\">");
    }

    /// <summary>
    /// The resolver leaves a placeholder it cannot fill as written. In a template that text is the
    /// author's, and it must not become markup or step out of the attribute it sits in.
    /// </summary>
    [Fact]
    public void A_placeholder_left_as_written_cannot_add_markup_or_leave_its_attribute()
    {
        var finished = EmailTemplateRenderer.Finish(
            "<p>{{<img src=x onerror=alert(1)>}}</p><a href=\"{{x\" onclick=\"alert(1)}}\">y</a>");

        finished.Should().Be("<p>{{&lt;img src=x onerror=alert(1)&gt;}}</p><a>y</a>");
    }

    /// <summary>
    /// The whole path a send takes, with no database: markdown, the shell, the resolve with the
    /// inline body's encoding, and the finish. A value holding markup arrives escaped.
    /// </summary>
    [Fact]
    public void A_script_in_a_value_arrives_escaped_and_the_template_markup_stays()
    {
        var html = EmailTemplateRenderer.Markdown("Hello **{{data.Name}}**, see [the site](https://x.example).");
        var entry = new Content { Id = Guid.NewGuid(), ContentType = "signup", Data = new() { ["Name"] = "<script>alert(1)</script>" } };

        var (_, finished) = EmailTemplateRenderer.Resolve(new RenderedTemplate("s", html, []), entry, extractor: null);

        finished.Should().Be(
            "<p>Hello <strong>&lt;script&gt;alert(1)&lt;/script&gt;</strong>, see <a href=\"https://x.example\">the site</a>.</p>\n");
    }

    /// <summary>
    /// Red with values encoded as an inline body encodes them: a form value of <c>{{</c> and a later
    /// one of <c>}}</c> paired into one placeholder-like run, and the finish encoded the markup
    /// between them, link and all.
    /// </summary>
    [Fact]
    public void Braces_in_two_values_cannot_pair_up_and_swallow_the_markup_between_them()
    {
        var html = EmailTemplateRenderer.Markdown("**{{data.Name}}** wrote [here](https://x.example): {{data.Message}}");
        var entry = new Content
        {
            Id = Guid.NewGuid(),
            ContentType = "signup",
            Data = new() { ["Name"] = "{{", ["Message"] = "}}" },
        };

        var (_, finished) = EmailTemplateRenderer.Resolve(new RenderedTemplate("s", html, []), entry, extractor: null);

        finished.Should().Be(
            "<p><strong>&#123;&#123;</strong> wrote <a href=\"https://x.example\">here</a>: &#125;&#125;</p>\n");
    }

    /// <summary>The subject is a header, so its values keep their braces, as an inline subject's do.</summary>
    [Fact]
    public void A_subject_value_keeps_its_braces()
    {
        var entry = new Content { Id = Guid.NewGuid(), ContentType = "signup", Data = new() { ["Name"] = "{x}" } };

        var (subject, _) = EmailTemplateRenderer.Resolve(new RenderedTemplate("Hi {{data.Name}}", "b", []), entry, extractor: null);

        subject.Should().Be("Hi {x}");
    }

    [Fact]
    public void The_blueprint_caps_each_text_a_template_is_made_of_at_the_parameter_cap()
    {
        using var stream = typeof(EmailTemplateRenderer).Assembly.GetManifestResourceStream("Blueprints/email.json")!;
        var blueprint = System.Text.Json.JsonSerializer.Deserialize<barakoCMS.Features.ContentType.Blueprints.Blueprint>(
            stream, barakoCMS.Features.ContentType.Blueprints.BlueprintCatalog.Json)!;

        var capped = blueprint.ContentTypes
            .SelectMany(t => t.Fields.Select(f => (Type: t.Name, f.Name, f.ValidationRules)))
            .Where(f => f.Name is "Subject" or "Body" or "Header" or "Footer")
            .ToList();

        capped.Should().HaveCount(4);
        capped.Should().OnlyContain(f =>
            f.ValidationRules.ContainsKey("maxLength")
            && f.ValidationRules["maxLength"].ToString() == TemplateExpression.MaxTemplateLength.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
