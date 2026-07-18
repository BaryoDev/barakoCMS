using BarakoCMS.Diagnostics;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests.Features.Diagnostics;

// Pure logic — no host, no database, so these run fast and don't need Docker.
public class ClientErrorTextTests
{
    [Fact]
    public void Fingerprint_IsStable_ForSameFault()
    {
        var a = ClientErrorText.Fingerprint("error", "Boom", "app.js:10", 500);
        var b = ClientErrorText.Fingerprint("error", "Boom", "app.js:10", 500);
        a.Should().Be(b);
        a.Should().HaveLength(32);
        a.Should().MatchRegex("^[0-9a-f]{32}$");
    }

    [Theory]
    [InlineData("error", "Boom", "app.js:10", 500, "error", "Boom", "app.js:10", 500)] // same
    [InlineData("error", "Boom", "app.js:10", 500, "react", "Boom", "app.js:10", 500)] // kind differs
    [InlineData("error", "Boom", "app.js:10", 500, "error", "Bang", "app.js:10", 500)] // message differs
    [InlineData("error", "Boom", "app.js:10", 500, "error", "Boom", "app.js:11", 500)] // source differs
    [InlineData("error", "Boom", "app.js:10", 500, "error", "Boom", "app.js:10", 502)] // status differs
    public void Fingerprint_ChangesWhenAnyPartChanges(
        string k1, string m1, string s1, int st1,
        string k2, string m2, string s2, int st2)
    {
        var a = ClientErrorText.Fingerprint(k1, m1, s1, st1);
        var b = ClientErrorText.Fingerprint(k2, m2, s2, st2);
        var same = k1 == k2 && m1 == m2 && s1 == s2 && st1 == st2;
        (a == b).Should().Be(same);
    }

    [Fact]
    public void Trim_CapsLengthAndTrimsWhitespace()
    {
        ClientErrorText.Trim("  hello  ", 100).Should().Be("hello");
        ClientErrorText.Trim(new string('x', 50), 10).Should().Be(new string('x', 10));
        ClientErrorText.Trim(null, 10).Should().BeNull();
        ClientErrorText.Trim("", 10).Should().Be("");
    }

    [Theory]
    [InlineData("warning", "warning")]
    [InlineData("error", "error")]
    [InlineData("info", "error")]
    [InlineData(null, "error")]
    [InlineData("WARNING", "error")] // case-sensitive on purpose; client only ever sends lowercase
    public void NormalizeSeverity_OnlyWarningIsWarning(string? input, string expected)
    {
        ClientErrorText.NormalizeSeverity(input).Should().Be(expected);
    }
}
