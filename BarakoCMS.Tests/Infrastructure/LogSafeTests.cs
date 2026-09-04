using barakoCMS.Infrastructure.Security;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests.Infrastructure;

public class LogSafeTests
{
    [Fact]
    public void A_newline_and_an_escape_sequence_become_spaces()
    {
        var text = LogSafe.Text("first\r\n\u001b[31msecond\tthird");

        text.Should().Be("first   [31msecond third");
        text.Should().NotContain("\n").And.NotContain("\r").And.NotContain("\u001b");
    }

    [Fact]
    public void A_C1_control_character_is_replaced_too()
    {
        LogSafe.Text("a\u0085b\u009bc").Should().Be("a b c");
    }

    [Fact]
    public void Ordinary_text_passes_through_unchanged()
    {
        LogSafe.Text("tenant-slug/with spaces and unicode: café").Should().Be("tenant-slug/with spaces and unicode: café");
    }

    [Fact]
    public void Long_text_is_cut_and_marked()
    {
        var text = LogSafe.Text(new string('x', 500), maxLength: 10);

        text.Should().Be("xxxxxxxxxx...");
        LogSafe.Text(new string('x', LogSafe.DefaultMaxLength + 1)).Should().HaveLength(LogSafe.DefaultMaxLength + 3);
        LogSafe.Text(new string('x', LogSafe.DefaultMaxLength)).Should().HaveLength(LogSafe.DefaultMaxLength);
    }

    [Fact]
    public void Null_and_empty_give_empty()
    {
        LogSafe.Text(null).Should().BeEmpty();
        LogSafe.Text(string.Empty).Should().BeEmpty();
    }
}
