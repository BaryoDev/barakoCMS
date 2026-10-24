using System.Net;
using System.Security.Cryptography;
using System.Text;
using barakoCMS.Core.Interfaces;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The core receiver primitive: signature, replay window, and one run per provider event id.
/// </summary>
[Collection("Integration1")]
public class InboundWebhookReceiverTests
{
    private const string Secret = "whsec_" + "dGVzdC1zZWNyZXQtZm9yLXN2aXgtc2lnbmluZy0xMjM0NTY3OA==";

    private readonly IntegrationTestFixture _factory;

    public InboundWebhookReceiverTests(IntegrationTestFixture factory) => _factory = factory;

    private static InboundWebhookRequest Signed(string id, DateTimeOffset at, string body, string secret = Secret)
    {
        var ts = at.ToUnixTimeSeconds().ToString();
        using var hmac = new HMACSHA256(Convert.FromBase64String(secret["whsec_".Length..]));
        var sig = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{id}.{ts}.{body}")));
        return new InboundWebhookRequest("test-" + Guid.NewGuid().ToString("N"), InboundWebhookScheme.Svix, Secret, body,
            new Dictionary<string, string>
            {
                ["svix-id"] = id,
                ["svix-timestamp"] = ts,
                ["svix-signature"] = "v1," + sig,
            });
    }

    private async Task<(InboundWebhookOutcome Outcome, int Ran)> ReceiveAsync(InboundWebhookRequest request, int times = 1)
    {
        var ran = 0;
        var outcome = InboundWebhookOutcome.Processed;
        for (var i = 0; i < times; i++)
        {
            using var scope = _factory.Services.CreateScope();
            var verifier = scope.ServiceProvider.GetRequiredService<IInboundWebhookVerifier>();
            outcome = await verifier.ReceiveAsync(request, _ => { ran++; return Task.CompletedTask; },
                TestContext.Current.CancellationToken);
        }
        return (outcome, ran);
    }

    [Fact]
    public async Task A_valid_signature_is_accepted_and_the_handler_runs()
    {
        var (outcome, ran) = await ReceiveAsync(Signed("msg_a", DateTimeOffset.UtcNow, "{}"));

        outcome.Should().Be(InboundWebhookOutcome.Processed);
        ran.Should().Be(1);
    }

    [Fact]
    public async Task A_bad_signature_is_refused_and_the_handler_does_not_run()
    {
        var good = Signed("msg_b", DateTimeOffset.UtcNow, "{}");
        var bad = good with { Headers = new Dictionary<string, string>(good.Headers) { ["svix-signature"] = "v1,AAAA" } };

        var (outcome, ran) = await ReceiveAsync(bad);

        outcome.Should().Be(InboundWebhookOutcome.BadSignature);
        ran.Should().Be(0);
    }

    [Fact]
    public async Task A_timestamp_outside_the_window_is_refused_even_when_the_signature_is_genuine()
    {
        var (outcome, ran) = await ReceiveAsync(Signed("msg_c", DateTimeOffset.UtcNow.AddHours(-1), "{}"));

        outcome.Should().Be(InboundWebhookOutcome.OutsideWindow);
        ran.Should().Be(0);
    }

    [Fact]
    public async Task The_same_event_id_sent_twice_runs_the_handler_once_and_answers_duplicate()
    {
        var request = Signed("msg_d", DateTimeOffset.UtcNow, "{}");

        var (outcome, ran) = await ReceiveAsync(request, times: 2);

        ran.Should().Be(1, "the second delivery is a replay of the same provider event");
        outcome.Should().Be(InboundWebhookOutcome.Duplicate);
    }

    [Fact]
    public async Task A_handler_that_throws_records_nothing_so_the_retry_runs_again()
    {
        var request = Signed("msg_e", DateTimeOffset.UtcNow, "{}");
        using (var scope = _factory.Services.CreateScope())
        {
            var verifier = scope.ServiceProvider.GetRequiredService<IInboundWebhookVerifier>();
            var act = () => verifier.ReceiveAsync(request, _ => throw new InvalidOperationException("boom"),
                TestContext.Current.CancellationToken);
            await act.Should().ThrowAsync<InvalidOperationException>();
        }

        var (outcome, ran) = await ReceiveAsync(request);

        outcome.Should().Be(InboundWebhookOutcome.Processed);
        ran.Should().Be(1);
    }
}
