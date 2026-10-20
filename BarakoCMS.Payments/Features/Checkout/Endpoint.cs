using barakoCMS.Models;
using FastEndpoints;
using FluentValidation;
using FluentValidation.Results;
using Marten;
using Microsoft.AspNetCore.Builder;

namespace BarakoCMS.Payments.Features.Checkout;

internal sealed class Request
{
    public Guid EntryId { get; set; }

    public string Provider { get; set; } = string.Empty;
}

internal sealed class Response
{
    public Guid AttemptId { get; set; }

    public string CheckoutUrl { get; set; } = string.Empty;
}

internal sealed class Validator : Validator<Request>
{
    public Validator()
    {
        RuleFor(x => x.EntryId).NotEmpty();
        RuleFor(x => x.Provider).NotEmpty();
    }
}

/// <summary>
/// POST /api/public/payments/checkout. Anonymous. Starts a payment for one entry and returns the
/// provider's hosted checkout URL.
/// </summary>
/// <remarks>
/// The request names an entry and a provider and nothing else. The amount, the currency and the
/// description come from the entry and its type's payable declaration, so a body that carries an
/// amount is read as if it did not. An entry whose type is not payable, or that is not in the state
/// the paid transition starts from, or that has already been paid, answers the same way it would
/// if it did not exist or could not be paid, so the route does not describe other entries.
/// </remarks>
internal sealed class Endpoint(
    IDocumentSession session,
    IEnumerable<IPaymentProvider> providers) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Post("/api/public/payments/checkout");
        AllowAnonymous();
        Options(x => x.RequireRateLimiting(PaymentsModule.CheckoutRateLimitPolicy));
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var provider = providers.FirstOrDefault(p => p.Name.Equals(req.Provider, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            ValidationFailures.Add(new ValidationFailure("provider", $"'{req.Provider}' is not a payment provider here."));
            await Send.ErrorsAsync(400, ct);
            return;
        }

        var entry = await session.LoadAsync<Content>(req.EntryId, ct);
        var payable = entry is null ? null : await session.LoadAsync<PayableType>(entry.ContentType, ct);
        if (entry is null || payable is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var definition = await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name == entry.ContentType, ct);
        var paid = definition?.Lifecycle?.Transitions
            .FirstOrDefault(t => t.Name.Equals(payable.PaidTransition, StringComparison.OrdinalIgnoreCase));
        var state = entry.LifecycleState ?? definition?.Lifecycle?.InitialState;
        if (paid is null || !paid.From.Equals(state, StringComparison.OrdinalIgnoreCase))
        {
            await Send.StringAsync("", 409, cancellation: ct);
            return;
        }

        var stored = entry.Data.FirstOrDefault(kv => kv.Key.Equals(payable.AmountField, StringComparison.OrdinalIgnoreCase)).Value;
        if (!AmountReader.TryRead(stored, out var amount) || amount <= 0)
        {
            ValidationFailures.Add(new ValidationFailure("entryId", "The entry has no amount to pay."));
            await Send.ErrorsAsync(400, ct);
            return;
        }

        var attempt = new PaymentAttempt
        {
            Id = Guid.NewGuid(),
            Provider = provider.Name,
            EntryId = entry.Id,
            ContentType = entry.ContentType,
            Amount = amount,
            Currency = payable.Currency,
            Status = PaymentAttemptStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var checkout = await provider.CreateCheckoutAsync(
            new CheckoutRequest(attempt.Id, amount, payable.Currency, payable.Description), ct);

        attempt.Reference = checkout.Reference;
        session.Store(attempt);
        await session.SaveChangesAsync(ct);

        await Send.OkAsync(new Response { AttemptId = attempt.Id, CheckoutUrl = checkout.Url }, ct);
    }
}
