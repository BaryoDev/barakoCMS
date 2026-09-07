using FluentAssertions;
using Xunit;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Infrastructure.Logging;

namespace BarakoCMS.Tests;

/// <summary>
/// Two hardening items raised by CodeQL against 4.0.
/// </summary>
public class CodeScanningHardeningTests
{
    /// <summary>
    /// A workflow action's parameters are free-form, so a credential arrives under whatever name the
    /// third party uses. Redacting only the exact string "Secret" left a Password or a Token in the
    /// execution log, which is served over the API to anyone who can read workflow runs.
    /// </summary>
    [Theory]
    [InlineData("Secret")]
    [InlineData("secret")]
    [InlineData("ClientSecret")]
    [InlineData("Password")]
    [InlineData("password")]
    [InlineData("Passwd")]
    [InlineData("Pwd")]
    [InlineData("Token")]
    [InlineData("AccessToken")]
    [InlineData("ApiKey")]
    [InlineData("api_key")]
    [InlineData("Credential")]
    [InlineData("PrivateKey")]
    [InlineData("private_key")]
    [InlineData("AccessKey")]
    public void Credential_named_parameters_are_never_stored_or_shown(string name)
    {
        WebhookSigning.IsSensitiveParameterName(name).Should().BeTrue();

        var kept = WebhookSigning.WithoutSecret(new Dictionary<string, string>
        {
            [name] = "the-actual-credential",
            ["Url"] = "https://example.test/hook",
        });

        kept.Should().NotContainKey(name);
        kept.Values.Should().NotContain("the-actual-credential");
        kept.Should().ContainKey("Url", "only credential-named parameters are dropped");
    }

    [Theory]
    [InlineData("Url")]
    [InlineData("Method")]
    [InlineData("Body")]
    [InlineData("ContentType")]
    public void Ordinary_parameters_are_kept(string name)
    {
        WebhookSigning.IsSensitiveParameterName(name).Should().BeFalse();
        WebhookSigning.WithoutSecret(new Dictionary<string, string> { [name] = "v" })
            .Should().ContainKey(name);
    }

    /// <summary>
    /// ASP.NET URL-decodes the request path, so a request for /a%0AFATAL arrives carrying a real
    /// newline. Written to a one-line-per-entry sink that becomes a second, forged entry (CWE-117).
    /// </summary>
    [Fact]
    public void A_newline_in_a_logged_value_cannot_forge_a_second_log_line()
    {
        var forged = LogSafe.Value("/api/contents\nFATAL Everything is fine");

        forged.Should().NotContain("\n");
        forged.Should().NotContain("\r");
        forged.Should().Contain("/api/contents");
        forged.Should().Contain("FATAL Everything is fine", "the text is kept, it just cannot start a line");
    }

    [Theory]
    [InlineData("\r")]
    [InlineData("\n")]
    [InlineData("\t")]
    [InlineData("\u001b")] // escape, which a terminal sink would act on
    public void Control_characters_become_a_space(string control)
    {
        LogSafe.Value($"a{control}b").Should().Be("a b");
    }

    [Fact]
    public void Ordinary_text_is_untouched()
    {
        LogSafe.Value("/api/contents?page=2").Should().Be("/api/contents?page=2");
    }

    [Fact]
    public void A_long_value_is_capped()
    {
        var result = LogSafe.Value(new string('x', LogSafe.MaxLength * 3));
        result.Length.Should().BeLessThan(LogSafe.MaxLength * 3);
        result.Should().EndWith("...");
    }

    [Fact]
    public void Null_and_empty_are_safe()
    {
        LogSafe.Value(null).Should().BeEmpty();
        LogSafe.Value("").Should().BeEmpty();
    }
}
