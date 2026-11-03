using barakoCMS.Features.Public;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

public class XmlTextTests
{
    [Fact]
    public void The_markup_characters_are_escaped()
    {
        XmlText.Escape("a&b<c>d\"e").Should().Be("a&amp;b&lt;c&gt;d&quot;e");
    }

    [Fact]
    public void A_tab_a_line_feed_and_a_carriage_return_are_kept()
    {
        XmlText.Escape("a\tb\nc\rd").Should().Be("a\tb\nc\rd");
    }

    [Fact]
    public void A_valid_surrogate_pair_is_kept()
    {
        XmlText.Escape("x\U0001F600y").Should().Be("x\U0001F600y");
    }

    [Theory]
    [InlineData(0xFFFE)]
    [InlineData(0xFFFF)]
    public void A_non_character_code_point_is_dropped(int code)
    {
        XmlText.Escape("a" + (char)code + "b").Should().Be("ab");
    }

    [Theory]
    [InlineData("a\u0000b", "ab")]
    [InlineData("a\u0008b", "ab")]
    [InlineData("a\u001Fb", "ab")]
    public void A_character_xml_cannot_carry_is_dropped(string input, string expected)
    {
        XmlText.Escape(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(0xD83D)]
    [InlineData(0xDE00)]
    public void A_lone_surrogate_is_dropped(int code)
    {
        XmlText.Escape("a" + (char)code + "b").Should().Be("ab");
    }
}
