using FastEndpoints;
using Marten;
using Marten.Patching;
using Microsoft.Extensions.Logging;

namespace barakoCMS.Infrastructure.Filters;

/// <summary>
/// Settles the idempotency claim that <see cref="IdempotencyFilter"/> made before the handler ran:
/// on a 2xx, marks it completed and stores the response so a retry can be answered with it;
/// otherwise <b>deletes it</b> so a legitimate retry can run.
///
/// Runs as a global post-processor, which FastEndpoints invokes even when the handler threw or
/// returned a 4xx, exactly the cases where the claim must be released.
/// </summary>
public class IdempotencyFinalizer : IGlobalPostProcessor
{
    public async Task PostProcessAsync(IPostProcessorContext context, CancellationToken ct)
    {
        var http = context.HttpContext;

        // Only requests that actually claimed a key (a fresh, non-duplicate request) get here with a
        // stashed key. A rejected or replayed duplicate never stashed one, so it can't touch the
        // completed record it collided with.
        if (!http.Items.TryGetValue(IdempotencyFilter.ScopedKeyItem, out var keyObj) ||
            keyObj is not string scopedKey)
            return;

        var capture = http.Items.TryGetValue(IdempotencyFilter.CaptureItem, out var captureObj)
            ? captureObj as IdempotencyResponseCapture
            : null;

        if (!http.Items.TryGetValue(IdempotencyFilter.ClaimIdItem, out var claimObj) || claimObj is not Guid claimId)
            return;

        var store = http.RequestServices.GetService<IDocumentStore>();
        if (store is null)
            return;

        var status = http.Response.StatusCode;
        var succeeded = Succeeded(status, context.HasExceptionOccurred, context.HasValidationFailures);

        var logger = http.RequestServices.GetService<ILogger<IdempotencyFinalizer>>();
        await using var session = store.LightweightSession();

        if (capture is not null)
        {
            // A JSON response may still sit in the response pipe; flush it through the tee before
            // reading what was kept.
            if (succeeded) await http.Response.BodyWriter.FlushAsync(ct);
            http.Response.Body = capture.Inner;
        }

        if (succeeded)
        {
            // Only a replayable claim had its response copied. Anonymous callers and routes that
            // return a credential keep the key and nothing else.
            StoredResponse? response = null;
            if (capture is not null)
            {
                var location = http.Response.Headers.Location.Count > 0 ? http.Response.Headers.Location.ToString() : null;
                var protector = http.RequestServices.GetService<IdempotencyProtector>();
                response = new StoredResponse(
                    status,
                    http.Response.ContentType,
                    location,
                    capture.Overflowed || protector is null ? null : protector.Seal(capture.Captured, scopedKey),
                    capture.Overflowed);
            }

            await CompleteAsync(session, scopedKey, claimId, response, ct);
        }
        else
        {
            // The request did not succeed, so release the key and let a retry run.
            await ReleaseAsync(session, scopedKey, claimId, ct);
            logger?.LogDebug("Released idempotency key after a failed request: {Key}", scopedKey);
        }
    }

    internal sealed record StoredResponse(int Status, string? ContentType, string? Location, string? SealedBody, bool TooLarge);

    /// <summary>Marks the claim completed, and keeps the response, only if this request still holds it.</summary>
    /// <remarks>
    /// Conditional on <paramref name="claimId"/>: a request that ran past the orphan window may have
    /// lost its claim to a retry, and must not complete the retry's claim with its own response.
    /// </remarks>
    internal static async Task CompleteAsync(
        IDocumentSession session, string scopedKey, Guid claimId, StoredResponse? response, CancellationToken ct)
    {
        var patch = session.Patch<Models.IdempotencyRecord>(r => r.Key == scopedKey && r.ClaimId == claimId)
            .Set(r => r.Completed, true);

        if (response is not null)
        {
            patch = patch
                .Set(r => r.StatusCode, (int?)response.Status)
                .Set(r => r.ContentType, response.ContentType)
                .Set(r => r.Location, response.Location)
                .Set(r => r.ProtectedResponseBody, response.SealedBody)
                .Set(r => r.ResponseTooLarge, response.TooLarge);
        }

        await session.SaveChangesAsync(ct);
    }

    /// <summary>Deletes the claim, only if this request still holds it.</summary>
    internal static async Task ReleaseAsync(IDocumentSession session, string scopedKey, Guid claimId, CancellationToken ct)
    {
        session.DeleteWhere<Models.IdempotencyRecord>(r => r.Key == scopedKey && r.ClaimId == claimId);
        await session.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Whether the claim is kept. Only a 2xx is: a 3xx, like a 4xx or 5xx, releases the key so the
    /// client can retry.
    /// </summary>
    internal static bool Succeeded(int status, bool exceptionOccurred, bool validationFailed) =>
        !exceptionOccurred && !validationFailed && status is >= 200 and < 300;
}
