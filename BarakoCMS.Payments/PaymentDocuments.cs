using barakoCMS.Models;

namespace BarakoCMS.Payments;

/// <summary>
/// Marks one content type as payable. A type with no row is not.
/// </summary>
/// <remarks>
/// Owned by the module so core gains nothing payment specific. The amount is read from
/// <see cref="AmountField"/> on the entry every time a checkout starts; nothing a caller sends is
/// used for it.
/// </remarks>
public sealed class PayableType
{
    /// <summary>The content type name.</summary>
    public string ContentType { get; set; } = string.Empty;

    /// <summary>The money, decimal or int field that holds what the entry costs.</summary>
    public string AmountField { get; set; } = string.Empty;

    /// <summary>ISO 4217 code, upper case.</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>Shown on the provider's page.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>The lifecycle transition run when the provider reports the payment.</summary>
    public string PaidTransition { get; set; } = string.Empty;

    /// <summary>The transition run when the provider reports a failure or expiry, or null to only record it.</summary>
    public string? FailedTransition { get; set; }
}

/// <summary>Numbered explicitly: Marten stores the integer.</summary>
public enum PaymentAttemptStatus
{
    Pending = 0,
    Paid = 1,
    Failed = 2,
}

/// <summary>One try at paying for one entry.</summary>
public sealed class PaymentAttempt
{
    public Guid Id { get; set; }

    public string Provider { get; set; } = string.Empty;

    /// <summary>The provider's reference for the checkout. Webhooks find the attempt by it.</summary>
    public string Reference { get; set; } = string.Empty;

    public Guid EntryId { get; set; }

    public string ContentType { get; set; } = string.Empty;

    public decimal Amount { get; set; }

    public string Currency { get; set; } = string.Empty;

    public PaymentAttemptStatus Status { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? SettledAt { get; set; }

    /// <summary>Sensitive unless someone with the right changes it. No delivery path reads this document.</summary>
    public SensitivityLevel Sensitivity { get; set; } = SensitivityLevel.Sensitive;
}

/// <summary>
/// A provider event that has been handled. Its id is <c>{provider}:{eventId}</c>, so storing a
/// second one for the same event fails instead of running the transition twice.
/// </summary>
public sealed class PaymentEventRecord
{
    public string Id { get; set; } = string.Empty;

    public Guid AttemptId { get; set; }

    public DateTimeOffset HandledAt { get; set; }

    public static string IdFor(string provider, string eventId) => $"{provider}:{eventId}";
}
