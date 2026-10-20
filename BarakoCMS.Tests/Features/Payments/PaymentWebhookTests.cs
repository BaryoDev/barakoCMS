using System.Net;
using BarakoCMS.Payments;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests.Features.Payments;

/// <summary>
/// POST /api/payments/webhooks/{provider}: signed, handled once per provider event id.
/// </summary>
[Collection("Sequential")]
public class PaymentWebhookTests
{
    private readonly PaymentHarness _payments;

    public PaymentWebhookTests(IntegrationTestFixture factory) => _payments = new PaymentHarness(factory);

    [Fact]
    public async Task A_signed_paid_event_runs_the_paid_transition_once_and_a_replay_changes_nothing()
    {
        var type = await _payments.PayableTypeAsync();
        var entryId = await _payments.EntryAsync(type);
        var (attemptId, reference) = await _payments.CheckoutAsync(entryId);
        (await _payments.StateAsync(entryId)).Should().Be("Pending");

        var first = await _payments.SignedWebhookAsync("evt_1", reference, "paid");

        first.StatusCode.Should().Be(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        (await _payments.StateAsync(entryId)).Should().Be("Paid");
        var settled = await _payments.AttemptAsync(attemptId);
        settled.Status.Should().Be(PaymentAttemptStatus.Paid);
        settled.SettledAt.Should().NotBeNull();
        (await _payments.EventRecordsAsync(attemptId)).Should().HaveCount(1);

        // Run twice, the paid transition would find the entry already Paid and answer 409.
        var replay = await _payments.SignedWebhookAsync("evt_1", reference, "paid");

        replay.StatusCode.Should().Be(HttpStatusCode.OK);
        (await _payments.StateAsync(entryId)).Should().Be("Paid");
        (await _payments.EventRecordsAsync(attemptId)).Should().HaveCount(1);
        (await _payments.AttemptAsync(attemptId)).SettledAt.Should().Be(settled.SettledAt);
    }

    [Fact]
    public async Task A_tampered_event_is_refused_and_changes_nothing()
    {
        var type = await _payments.PayableTypeAsync();
        var entryId = await _payments.EntryAsync(type);
        var (attemptId, reference) = await _payments.CheckoutAsync(entryId);
        var signedFailed = PaymentHarness.Body("evt_t", reference, "failed");
        var signature = FakePaymentProvider.Sign(signedFailed);

        // The signature is for a failed event; the body now says paid.
        var response = await _payments.WebhookAsync(PaymentHarness.Body("evt_t", reference, "paid"), signature);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _payments.StateAsync(entryId)).Should().Be("Pending");
        (await _payments.AttemptAsync(attemptId)).Status.Should().Be(PaymentAttemptStatus.Pending);
        (await _payments.EventRecordsAsync(attemptId)).Should().BeEmpty();
    }

    [Fact]
    public async Task An_unsigned_event_is_refused_and_changes_nothing()
    {
        var type = await _payments.PayableTypeAsync();
        var entryId = await _payments.EntryAsync(type);
        var (attemptId, reference) = await _payments.CheckoutAsync(entryId);

        var response = await _payments.WebhookAsync(PaymentHarness.Body("evt_u", reference, "paid"), signature: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _payments.StateAsync(entryId)).Should().Be("Pending");
        (await _payments.EventRecordsAsync(attemptId)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_signed_failed_event_runs_the_failed_transition()
    {
        var type = await _payments.PayableTypeAsync();
        var entryId = await _payments.EntryAsync(type);
        var (attemptId, reference) = await _payments.CheckoutAsync(entryId);

        var response = await _payments.SignedWebhookAsync("evt_f", reference, "failed");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await _payments.StateAsync(entryId)).Should().Be("Failed");
        (await _payments.AttemptAsync(attemptId)).Status.Should().Be(PaymentAttemptStatus.Failed);
    }

    [Fact]
    public async Task A_provider_nobody_registered_has_no_webhook()
    {
        var body = PaymentHarness.Body("evt_x", "ref", "paid");

        var response = await _payments.WebhookAsync(body, FakePaymentProvider.Sign(body), provider: "nope");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_signed_event_for_a_reference_nobody_started_is_not_found()
    {
        var response = await _payments.SignedWebhookAsync("evt_n", "fake_unknown", "paid");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
