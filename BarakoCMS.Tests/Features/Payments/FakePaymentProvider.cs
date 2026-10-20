using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BarakoCMS.Payments;

namespace BarakoCMS.Tests.Features.Payments;

/// <summary>
/// A provider that signs with a fixed secret, so a test can sign a webhook, tamper with one, or send
/// one unsigned.
/// </summary>
internal sealed class FakePaymentProvider : IPaymentProvider
{
    public const string Secret = "fake-provider-secret";
    public const string SignatureHeader = "X-Fake-Signature";

    public string Name => "fake";

    /// <summary>What each checkout was asked to charge, by attempt id.</summary>
    public ConcurrentDictionary<Guid, CheckoutRequest> Requests { get; } = new();

    public Task<CheckoutSession> CreateCheckoutAsync(CheckoutRequest request, CancellationToken ct)
    {
        Requests[request.AttemptId] = request;
        var reference = $"fake_{Guid.NewGuid():N}";
        return Task.FromResult(new CheckoutSession(reference, $"https://pay.fake.test/{reference}"));
    }

    public PaymentEvent? ReadWebhook(string body, IReadOnlyDictionary<string, string> headers)
    {
        if (!headers.TryGetValue(SignatureHeader, out var signature) || !Sign(body).Equals(signature, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var kind = root.GetProperty("type").GetString() switch
        {
            "paid" => PaymentEventKind.Paid,
            "failed" => PaymentEventKind.Failed,
            _ => (PaymentEventKind?)null,
        };

        return kind is null
            ? null
            : new PaymentEvent(root.GetProperty("id").GetString()!, root.GetProperty("reference").GetString()!, kind.Value);
    }

    public static string Sign(string body) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(body)));
}
