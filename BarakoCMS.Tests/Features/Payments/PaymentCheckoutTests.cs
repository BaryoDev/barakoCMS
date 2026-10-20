using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Models;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests.Features.Payments;

/// <summary>
/// POST /api/public/payments/checkout, and the declaration that makes a type payable.
/// </summary>
[Collection("Sequential")]
public class PaymentCheckoutTests
{
    private readonly PaymentHarness _payments;

    public PaymentCheckoutTests(IntegrationTestFixture factory) => _payments = new PaymentHarness(factory);

    [Fact]
    public async Task A_changed_amount_in_the_request_still_charges_the_amount_on_the_entry()
    {
        var type = await _payments.PayableTypeAsync();
        var entryId = await _payments.EntryAsync(type);

        var response = await _payments.Visitor().PostAsJsonAsync("/api/public/payments/checkout", new
        {
            entryId,
            provider = "fake",
            amount = 1,
            currency = "USD",
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("checkoutUrl").GetString().Should().StartWith("https://pay.fake.test/");

        var attempts = await _payments.AttemptsForAsync(entryId);
        attempts.Should().HaveCount(1);
        attempts[0].Amount.Should().Be((decimal)PaymentHarness.Fee);
        attempts[0].Currency.Should().Be("PHP");
        attempts[0].Status.Should().Be(BarakoCMS.Payments.PaymentAttemptStatus.Pending);

        var asked = _payments.Fake.Requests[attempts[0].Id];
        asked.Amount.Should().Be((decimal)PaymentHarness.Fee);
        asked.Currency.Should().Be("PHP");
    }

    [Fact]
    public async Task A_payment_attempt_is_sensitive_by_default()
    {
        var type = await _payments.PayableTypeAsync();
        var entryId = await _payments.EntryAsync(type);

        var (attemptId, _) = await _payments.CheckoutAsync(entryId);

        (await _payments.AttemptAsync(attemptId)).Sensitivity.Should().Be(SensitivityLevel.Sensitive);
    }

    [Fact]
    public async Task An_entry_of_a_type_that_is_not_payable_cannot_be_checked_out()
    {
        var type = await _payments.PayableTypeAsync(declare: false);
        var entryId = await _payments.EntryAsync(type);

        var response = await _payments.Visitor().PostAsJsonAsync("/api/public/payments/checkout", new { entryId, provider = "fake" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _payments.AttemptsForAsync(entryId)).Should().BeEmpty();
    }

    [Fact]
    public async Task An_entry_that_is_already_paid_cannot_be_checked_out_again()
    {
        var type = await _payments.PayableTypeAsync();
        var entryId = await _payments.EntryAsync(type);
        var (_, reference) = await _payments.CheckoutAsync(entryId);
        (await _payments.SignedWebhookAsync("evt_paid", reference, "paid")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _payments.StateAsync(entryId)).Should().Be("Paid");

        var response = await _payments.Visitor().PostAsJsonAsync("/api/public/payments/checkout", new { entryId, provider = "fake" });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _payments.AttemptsForAsync(entryId)).Should().HaveCount(1);
    }

    [Fact]
    public async Task A_provider_nobody_registered_is_refused()
    {
        var type = await _payments.PayableTypeAsync();
        var entryId = await _payments.EntryAsync(type);

        var response = await _payments.Visitor().PostAsJsonAsync("/api/public/payments/checkout", new { entryId, provider = "nope" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _payments.AttemptsForAsync(entryId)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_declaration_naming_a_transition_the_type_lacks_is_refused()
    {
        var type = await _payments.PayableTypeAsync(declare: false);

        var response = await (await _payments.AdminAsync()).PutAsJsonAsync($"/api/payments/payable/{type}", new
        {
            enabled = true,
            amountField = "fee",
            currency = "PHP",
            paidTransition = "NoSuchMove",
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Declaring_a_type_payable_needs_a_signed_in_caller()
    {
        var type = await _payments.PayableTypeAsync(declare: false);

        var response = await _payments.Visitor().PutAsJsonAsync($"/api/payments/payable/{type}", new
        {
            enabled = true,
            amountField = "fee",
            currency = "PHP",
            paidTransition = "MarkPaid",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
