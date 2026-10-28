using BarakoCMS.Push;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BarakoCMS.Tests.Push;

public class PushOptionsTests
{
    private static IConfiguration Section(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void Missing_keys_mean_not_configured_and_do_not_throw()
    {
        var o = PushOptions.From(Section(new()));
        Assert.False(o.IsConfigured);
        Assert.Null(o.Subject);
    }

    [Fact]
    public void Valid_keys_are_configured()
    {
        var k = VapidKeyGenerator.Generate();
        var o = PushOptions.From(Section(new()
        {
            ["PublicKey"] = k.PublicKey,
            ["PrivateKey"] = k.PrivateKey,
            ["Subject"] = "mailto:ops@example.org",
        }));
        Assert.True(o.IsConfigured);
        Assert.Equal("mailto:ops@example.org", o.Subject);
    }

    [Fact]
    public void Malformed_private_key_refuses_and_never_echoes_the_value()
    {
        var k = VapidKeyGenerator.Generate();
        const string bad = "not-a-key-SECRETVALUE";
        var ex = Assert.Throws<InvalidOperationException>(() => PushOptions.From(Section(new()
        {
            ["PublicKey"] = k.PublicKey,
            ["PrivateKey"] = bad,
            ["Subject"] = "mailto:ops@example.org",
        })));
        Assert.Contains("PrivateKey", ex.Message);
        Assert.DoesNotContain("SECRETVALUE", ex.Message);
        Assert.DoesNotContain(k.PublicKey, ex.Message);
    }

    [Fact]
    public void Only_one_key_set_refuses()
    {
        var k = VapidKeyGenerator.Generate();
        Assert.Throws<InvalidOperationException>(() => PushOptions.From(Section(new()
        {
            ["PublicKey"] = k.PublicKey,
            ["Subject"] = "mailto:ops@example.org",
        })));
    }

    [Fact]
    public void Keys_without_a_subject_refuse()
    {
        var k = VapidKeyGenerator.Generate();
        var ex = Assert.Throws<InvalidOperationException>(() => PushOptions.From(Section(new()
        {
            ["PublicKey"] = k.PublicKey,
            ["PrivateKey"] = k.PrivateKey,
        })));
        Assert.Contains("Subject", ex.Message);
        Assert.DoesNotContain(k.PrivateKey, ex.Message);
    }

    [Theory]
    [InlineData("ops@example.org")]
    [InlineData("http://example.org")]
    [InlineData("ftp://example.org")]
    public void A_subject_that_is_not_mailto_or_https_refuses(string subject)
    {
        var k = VapidKeyGenerator.Generate();
        Assert.Throws<InvalidOperationException>(() => PushOptions.From(Section(new()
        {
            ["PublicKey"] = k.PublicKey,
            ["PrivateKey"] = k.PrivateKey,
            ["Subject"] = subject,
        })));
    }
}
