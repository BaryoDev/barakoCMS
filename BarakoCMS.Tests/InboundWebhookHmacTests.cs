using System.Security.Cryptography;
using System.Text;
using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Services;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

public class InboundWebhookHmacTests
{
    private const string Secret = "plain-shared-secret";
    private const string Body = "{\"event\":\"paid\"}";
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    private static string Sign(string signed)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Secret));
        return "sha256=" + Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(signed))).ToLowerInvariant();
    }

    private static InboundWebhookRequest BodyOnly(bool allow = false) =>
        new("test", InboundWebhookScheme.Hmac, Secret, Body,
            new Dictionary<string, string> { ["X-Signature"] = Sign(Body) })
        {
            AllowBodyOnlySignature = allow,
        };

    private static InboundWebhookRequest Timestamped(DateTimeOffset signedAt)
    {
        var ts = signedAt.ToUnixTimeSeconds().ToString();
        return new("test", InboundWebhookScheme.Hmac, Secret, Body,
            new Dictionary<string, string> { ["X-Signature"] = Sign($"{ts}.{Body}"), ["X-Timestamp"] = ts })
        {
            TimestampHeader = "X-Timestamp",
        };
    }

    [Fact]
    public void A_body_only_signature_is_refused_by_default()
    {
        InboundWebhookVerifier.Check(BodyOnly(), Now).Should().Be(InboundWebhookOutcome.BadSignature);
    }

    [Fact]
    public void A_timestamped_signature_inside_the_window_is_accepted()
    {
        InboundWebhookVerifier.Check(Timestamped(Now.AddMinutes(-4)), Now).Should().Be(InboundWebhookOutcome.Processed);
    }

    [Fact]
    public void A_timestamped_signature_outside_the_window_is_refused()
    {
        InboundWebhookVerifier.Check(Timestamped(Now.AddMinutes(-6)), Now).Should().Be(InboundWebhookOutcome.OutsideWindow);
        InboundWebhookVerifier.Check(Timestamped(Now.AddMinutes(6)), Now).Should().Be(InboundWebhookOutcome.OutsideWindow);
    }

    [Fact]
    public void A_body_only_signature_is_accepted_when_the_receiver_allows_it()
    {
        InboundWebhookVerifier.Check(BodyOnly(allow: true), Now).Should().Be(InboundWebhookOutcome.Processed);
    }

    [Fact]
    public void Allowing_a_body_only_signature_does_not_loosen_a_receiver_that_names_a_timestamp_header()
    {
        var stale = Timestamped(Now.AddHours(-1)) with { AllowBodyOnlySignature = true };

        InboundWebhookVerifier.Check(stale, Now).Should().Be(InboundWebhookOutcome.OutsideWindow);
    }
}
