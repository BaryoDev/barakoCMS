namespace BarakoCMS.Payments;

/// <summary>
/// One payment provider. An adapter creates the provider's hosted checkout and turns a webhook
/// into an event, after checking the provider's signature.
/// </summary>
/// <remarks>
/// Card and wallet details never reach barakoCMS: the visitor pays on the provider's page. Each
/// adapter is registered as an <see cref="IPaymentProvider"/> and is picked by <see cref="Name"/>,
/// which is also the <c>{provider}</c> in the webhook route.
/// </remarks>
public interface IPaymentProvider
{
    /// <summary>Lower case, letters and digits only. The <c>{provider}</c> of the webhook route.</summary>
    string Name { get; }

    /// <summary>Creates the provider's hosted checkout for an amount the server has already decided.</summary>
    Task<CheckoutSession> CreateCheckoutAsync(CheckoutRequest request, CancellationToken ct);

    /// <summary>
    /// Verifies the signature over the raw body and reads the event. Returns null when the
    /// signature does not match or the body is not an event, and never throws for either.
    /// </summary>
    PaymentEvent? ReadWebhook(string body, IReadOnlyDictionary<string, string> headers);
}

/// <summary>What the provider is asked to charge. Every value comes from the entry, not the caller.</summary>
public sealed record CheckoutRequest(Guid AttemptId, decimal Amount, string Currency, string Description);

/// <summary>The provider's answer to a checkout: its own reference and where the visitor pays.</summary>
public sealed record CheckoutSession(string Reference, string Url);

/// <summary>What a provider says happened to one payment.</summary>
public enum PaymentEventKind
{
    Paid = 1,
    Failed = 2,
}

/// <summary>
/// A verified webhook event. <see cref="EventId"/> is the provider's id for the event itself, which
/// is what makes a redelivery recognisable. <see cref="Reference"/> is the one returned by
/// <see cref="IPaymentProvider.CreateCheckoutAsync"/>. An adapter that can read the amount and
/// currency from the event sets them, and a paid event that disagrees with the attempt is refused.
/// </summary>
public sealed record PaymentEvent(string EventId, string Reference, PaymentEventKind Kind)
{
    /// <summary>The amount the provider says it took, when the event carries one.</summary>
    public decimal? Amount { get; init; }

    /// <summary>The currency the provider says it took, when the event carries one.</summary>
    public string? Currency { get; init; }
}
