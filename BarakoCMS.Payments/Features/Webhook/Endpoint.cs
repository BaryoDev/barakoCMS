using barakoCMS.Core.Interfaces;
using barakoCMS.Models;
using FastEndpoints;
using Marten;
using Microsoft.Extensions.Logging;

namespace BarakoCMS.Payments.Features.Webhook;

/// <summary>
/// POST /api/payments/webhooks/{provider}. Anonymous, because the provider holds no token; the
/// signature the provider's adapter checks is the authentication.
/// </summary>
/// <remarks>
/// An event is handled once. Its provider event id is the key of a stored record, saved in the same
/// commit as the transition, so a redelivery finds the record and answers 200 without doing
/// anything, and two deliveries racing each other cannot both commit. A bad signature answers 401
/// and touches nothing. The transition runs as the system actor <c>payments-webhook</c>, on the
/// module's own authority, since no user is behind a webhook.
///
/// An event for an attempt that is no longer Pending writes nothing, so a later event cannot move
/// a settled payment. A failed event answers 200. A paid event answers 409 and logs a warning. A paid event whose amount or currency differs from the
/// attempt answers 409.
///
/// An event for a payment whose entry is no longer in the state the transition starts from answers
/// 409 and writes nothing, so the attempt stays pending for a person or for reconciliation.
/// </remarks>
internal sealed class Endpoint(
    IDocumentSession session,
    IContentTransitioner transitioner,
    IEnumerable<IPaymentProvider> providers,
    ILogger<Endpoint> logger) : EndpointWithoutRequest
{
    private const int MaxBodyBytes = 64 * 1024;

    private static readonly ContentTransitionActor Actor = ContentTransitionActor.ForSystem("payments-webhook");

    public override void Configure()
    {
        Post("/api/payments/webhooks/{provider}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var name = Route<string>("provider") ?? string.Empty;
        var provider = providers.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (HttpContext.Request.ContentLength > MaxBodyBytes)
        {
            await Send.StringAsync("", 413, cancellation: ct);
            return;
        }

        var body = await ReadBodyAsync(ct);
        if (body is null)
        {
            await Send.StringAsync("", 413, cancellation: ct);
            return;
        }

        var headers = HttpContext.Request.Headers.ToDictionary(
            h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);

        var received = provider.ReadWebhook(body, headers);
        if (received is null)
        {
            await Send.StringAsync("", 401, cancellation: ct);
            return;
        }

        var recordId = PaymentEventRecord.IdFor(provider.Name, received.EventId);
        if (await session.LoadAsync<PaymentEventRecord>(recordId, ct) is not null)
        {
            await Send.OkAsync(ct);
            return;
        }

        var attempt = await session.Query<PaymentAttempt>()
            .FirstOrDefaultAsync(a => a.Reference == received.Reference && a.Provider == provider.Name, ct);
        if (attempt is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (attempt.Status != PaymentAttemptStatus.Pending)
        {
            if (received.Kind == PaymentEventKind.Paid)
            {
                logger.LogWarning("Payment event {Event} reports payment for attempt {Attempt}, which is already {Status}.", recordId, attempt.Id, attempt.Status);
                await Send.StringAsync("", 409, cancellation: ct);
                return;
            }

            await Send.OkAsync(ct);
            return;
        }

        var payable = await session.LoadAsync<PayableType>(attempt.ContentType, ct);
        var entry = await session.LoadAsync<Content>(attempt.EntryId, ct);
        if (payable is null || entry is null)
        {
            logger.LogWarning("Payment event {Event} names attempt {Attempt}, whose entry or declaration is gone.", recordId, attempt.Id);
            await Send.StringAsync("", 409, cancellation: ct);
            return;
        }

        var paid = received.Kind == PaymentEventKind.Paid;
        if (paid && Disagrees(received, attempt))
        {
            logger.LogWarning("Payment event {Event} for attempt {Attempt} does not match the amount or currency charged.", recordId, attempt.Id);
            await Send.StringAsync("", 409, cancellation: ct);
            return;
        }

        attempt.Status = paid ? PaymentAttemptStatus.Paid : PaymentAttemptStatus.Failed;
        attempt.SettledAt = DateTimeOffset.UtcNow;
        session.Store(attempt);
        session.Insert(new PaymentEventRecord { Id = recordId, AttemptId = attempt.Id, HandledAt = DateTimeOffset.UtcNow });

        var transition = paid ? payable.PaidTransition : payable.FailedTransition;

        try
        {
            if (string.IsNullOrEmpty(transition))
            {
                await session.SaveChangesAsync(ct);
            }
            else
            {
                var result = await transitioner.TransitionAsync(
                    entry, transition, Actor, new ContentTransitionOptions { SkipPermissionChecks = true }, ct);

                if (!result.Succeeded)
                {
                    logger.LogWarning("Payment event {Event} could not run '{Transition}': {Outcome} {Errors}",
                        recordId, transition, result.Outcome, string.Join("; ", result.Errors));
                    await Send.StringAsync("", result.Outcome == ContentTransitionOutcome.Conflict ? 409 : 422, cancellation: ct);
                    return;
                }
            }
        }
        catch (JasperFx.DocumentAlreadyExistsException)
        {
            // Another delivery of the same event committed first.
        }

        await Send.OkAsync(ct);
    }

    private static bool Disagrees(PaymentEvent received, PaymentAttempt attempt) =>
        (received.Amount is { } amount && amount != attempt.Amount)
        || (received.Currency is { Length: > 0 } currency
            && !currency.Equals(attempt.Currency, StringComparison.OrdinalIgnoreCase));

    private async Task<string?> ReadBodyAsync(CancellationToken ct)
    {
        using var reader = new StreamReader(HttpContext.Request.Body);
        var buffer = new char[MaxBodyBytes + 1];
        var read = await reader.ReadBlockAsync(buffer.AsMemory(), ct);
        return read > MaxBodyBytes ? null : new string(buffer, 0, read);
    }
}
