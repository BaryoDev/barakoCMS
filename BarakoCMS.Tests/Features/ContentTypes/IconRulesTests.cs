using FluentAssertions;
using barakoCMS.Core.Validation;
using barakoCMS.Models;
using Xunit;

namespace BarakoCMS.Tests.Features.ContentTypes;

public class IconRulesTests
{
    private const string Good =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\"><path d=\"M1 1h22v22H1z\"/></svg>";

    private static ContentTypeIcon Svg(string svg) => new() { Svg = svg };

    [Fact]
    public void A_script_an_onload_handler_a_style_element_and_an_external_href_are_all_removed()
    {
        var hostile =
            "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" viewBox=\"0 0 24 24\" onload=\"alert(1)\">"
            + "<script>alert(1)</script><style>path{fill:red}</style>"
            + "<a xlink:href=\"https://evil.example/x\"><path d=\"M0 0h5v5z\"/></a>"
            + "<path d=\"M1 1h22v22H1z\" onclick=\"alert(2)\" xlink:href=\"https://evil.example/y\" fill=\"red\" stroke=\"blue\" style=\"fill:red\"/>"
            + "<foreignObject><div>hi</div></foreignObject>"
            + "</svg>";

        var result = IconRules.Check(Svg(hostile));

        result.Valid.Should().BeTrue(string.Join(" ", result.Errors));
        var clean = result.Icon!.Svg!;
        clean.Should().Contain("<path");
        clean.Should().NotContain("script");
        clean.Should().NotContain("alert");
        clean.Should().NotContain("onload");
        clean.Should().NotContain("onclick");
        clean.Should().NotContain("style");
        clean.Should().NotContain("href");
        clean.Should().NotContain("evil.example");
        clean.Should().NotContain("foreignObject");
        clean.Should().NotContain("stroke");
        clean.Should().NotContain("red");
        clean.Should().Contain("fill=\"currentColor\"");
    }

    [Fact]
    public void Every_shape_is_forced_to_currentColor_and_nested_groups_keep_their_shapes()
    {
        var svg = "<svg viewBox=\"0 0 10 10\"><g transform=\"translate(1 1)\"><circle cx=\"5\" cy=\"5\" r=\"2\" fill=\"#f00\"/><rect x=\"1\" y=\"1\" width=\"2\" height=\"2\"/></g></svg>";

        var clean = IconRules.Check(Svg(svg)).Icon!.Svg!;

        clean.Should().Contain("<g transform=\"translate(1 1)\">");
        System.Text.RegularExpressions.Regex.Matches(clean, "fill=\"currentColor\"").Should().HaveCount(2);
        clean.Should().NotContain("#f00");
    }

    [Fact]
    public void An_attribute_value_that_reaches_for_a_url_is_dropped()
    {
        var svg = "<svg viewBox=\"0 0 10 10\"><path d=\"M0 0h5v5z\" transform=\"url(https://evil.example/x)\"/></svg>";

        var clean = IconRules.Check(Svg(svg)).Icon!.Svg!;

        clean.Should().Contain("d=\"M0 0h5v5z\"");
        clean.Should().NotContain("transform");
        clean.Should().NotContain("evil.example");
    }

    [Fact]
    public void An_svg_with_no_viewBox_is_refused_by_name()
    {
        var result = IconRules.Check(Svg("<svg xmlns=\"http://www.w3.org/2000/svg\"><path d=\"M0 0h5v5z\"/></svg>"));

        result.Valid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Contain("viewBox");
    }

    [Fact]
    public void An_svg_with_no_shape_is_refused_by_name()
    {
        var result = IconRules.Check(Svg("<svg viewBox=\"0 0 24 24\"><script>alert(1)</script><g/></svg>"));

        result.Valid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Contain("no shape");
    }

    [Fact]
    public void Markup_that_is_not_XML_is_refused()
    {
        var result = IconRules.Check(Svg("<svg viewBox=\"0 0 24 24\"><path d=\"M0 0\">"));

        result.Valid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Contain("not well-formed XML");
    }

    [Fact]
    public void A_billion_laughs_document_is_refused_without_expanding()
    {
        var bomb =
            "<?xml version=\"1.0\"?><!DOCTYPE svg [<!ENTITY a \"aaaaaaaaaa\">"
            + "<!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;\"><!ENTITY c \"&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;\">"
            + "<!ENTITY d \"&c;&c;&c;&c;&c;&c;&c;&c;&c;&c;\"><!ENTITY e \"&d;&d;&d;&d;&d;&d;&d;&d;&d;&d;\">"
            + "<!ENTITY f \"&e;&e;&e;&e;&e;&e;&e;&e;&e;&e;\"><!ENTITY g \"&f;&f;&f;&f;&f;&f;&f;&f;&f;&f;\">"
            + "<!ENTITY h \"&g;&g;&g;&g;&g;&g;&g;&g;&g;&g;\"><!ENTITY i \"&h;&h;&h;&h;&h;&h;&h;&h;&h;&h;\">]>"
            + "<svg viewBox=\"0 0 24 24\"><title>&i;</title><path d=\"M0 0h5v5z\"/></svg>";

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = IconRules.Check(Svg(bomb));
        watch.Stop();

        result.Valid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Contain("DOCTYPE");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void An_external_entity_is_refused()
    {
        var xxe = "<!DOCTYPE svg [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><svg viewBox=\"0 0 24 24\"><title>&x;</title><path d=\"M0 0h5v5z\"/></svg>";

        IconRules.Check(Svg(xxe)).Valid.Should().BeFalse();
    }

    [Fact]
    public void An_svg_over_the_size_cap_is_refused_and_one_at_it_is_not()
    {
        var path = "<path d=\"M0 0h5v5z\"/>";
        var open = "<svg viewBox=\"0 0 24 24\">";
        var close = "</svg>";
        var filler = new string(' ', IconRules.MaxSvgLength - (open + path + close).Length);

        IconRules.Check(Svg(open + path + filler + close)).Valid.Should().BeTrue("exactly at the cap");
        IconRules.Check(Svg(open + path + filler + " " + close)).Valid.Should().BeFalse("one over");
    }

    [Theory]
    [InlineData("home")]
    [InlineData("Icon-24")]
    public void A_sprite_name_of_letters_digits_and_hyphens_is_kept(string name)
    {
        var result = IconRules.Check(new ContentTypeIcon { Name = name });

        result.Valid.Should().BeTrue();
        result.Icon!.Name.Should().Be(name);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("under_score")]
    [InlineData("<script>")]
    public void A_sprite_name_with_other_characters_is_refused(string name) =>
        IconRules.Check(new ContentTypeIcon { Name = name }).Valid.Should().BeFalse();

    [Fact]
    public void A_sprite_name_over_the_length_cap_is_refused()
    {
        IconRules.Check(new ContentTypeIcon { Name = new string('a', IconRules.MaxNameLength) }).Valid.Should().BeTrue();
        IconRules.Check(new ContentTypeIcon { Name = new string('a', IconRules.MaxNameLength + 1) }).Valid.Should().BeFalse();
    }

    [Fact]
    public void An_icon_with_both_a_name_and_an_svg_or_neither_is_refused()
    {
        IconRules.Check(new ContentTypeIcon { Name = "home", Svg = Good }).Valid.Should().BeFalse();
        IconRules.Check(new ContentTypeIcon()).Valid.Should().BeFalse();
    }

    [Fact]
    public void No_icon_is_valid_and_stays_null()
    {
        var result = IconRules.Check(null);

        result.Valid.Should().BeTrue();
        result.Icon.Should().BeNull();
    }
}
