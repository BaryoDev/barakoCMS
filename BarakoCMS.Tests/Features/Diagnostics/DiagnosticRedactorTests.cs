using BarakoCMS.Diagnostics.Redaction;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests.Features.Diagnostics;

/// <summary>
/// The rules the incidents view and the forwarder share. Each input holds a secret beside text that
/// must survive, so a rule that removed everything would fail as surely as one that removed nothing.
/// </summary>
public class DiagnosticRedactorTests
{
    [Theory]
    [InlineData("call failed with Authorization: Bearer abcdefghijklmnop123", "abcdefghijklmnop123")]
    [InlineData("token eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjMifQ.c2lnbmF0dXJl rejected", "eyJhbGciOiJIUzI1NiJ9")]
    [InlineData("GET https://app.example.com/reset?token=s3cr3tvalue&lang=en failed", "s3cr3tvalue")]
    [InlineData("connect https://admin:hunter2@db.example.com/ refused", "hunter2")]
    [InlineData("login failed: password=hunter22 for request", "hunter22")]
    [InlineData("{\"apiKey\": \"live-key-123\"} was refused", "live-key-123")]
    [InlineData("sent to juan.delacruz@example.ph and bounced", "juan.delacruz@example.ph")]
    [InlineData("session 7f3a9c2e5b1d4f6a8c0e2b4d6f8a1c3e5b7d9f1a expired", "7f3a9c2e5b1d4f6a8c0e2b4d6f8a1c3e5b7d9f1a")]
    public void A_secret_is_removed_and_the_text_around_it_survives(string text, string secret)
    {
        var redacted = DiagnosticRedactor.Redact(text);

        redacted.Should().NotContain(secret);
        redacted.Should().Contain(DiagnosticRedactor.Mask);
        redacted!.Length.Should().BeGreaterThan(DiagnosticRedactor.Mask.Length, "the text around the secret is kept");
    }

    [Fact]
    public void A_value_after_a_Sensitive_field_name_is_removed_in_each_spelling()
    {
        string[] fields = ["MobileNumber"];

        DiagnosticRedactor.Redact("save failed {\"MobileNumber\": \"+63 917 555 0101\"}", fields)
            .Should().NotContain("917 555 0101");
        DiagnosticRedactor.Redact("save failed MobileNumber=09175550101 rejected", fields)
            .Should().NotContain("09175550101").And.Contain("rejected");
        DiagnosticRedactor.Redact("save failed mobilenumber: 09175550101", fields)
            .Should().NotContain("09175550101");
    }

    [Fact]
    public void Ordinary_error_text_is_left_alone()
    {
        const string text = "TypeError: Cannot read properties of undefined (reading 'map') at BarakoCMSFeaturesContentGetResponse.render (app.js:12:3), status code 500";

        DiagnosticRedactor.Redact(text).Should().Be(text);
    }

    [Fact]
    public void Null_and_empty_pass_through()
    {
        DiagnosticRedactor.Redact(null).Should().BeNull();
        DiagnosticRedactor.Redact(string.Empty).Should().BeEmpty();
    }
}
