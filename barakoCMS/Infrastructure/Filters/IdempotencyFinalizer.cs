using FastEndpoints;
using Marten;
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

        var store = http.RequestServices.GetService<IDocumentStore>();
        if (store is null)
            return;

        var status = http.Response.StatusCode;
        var succeeded = Succeeded(status, context.HasExceptionOccurred, context.HasValidationFailures);

        var logger = http.RequestServices.GetService<ILogger<IdempotencyFinalizer>>();
        await using var session = store.LightweightSession();

        if (succeeded)
        {
            if (capture is not null)
            {
                // A JSON response may still sit in the response pipe; flush it through the tee before
                // reading what was kept.
                await http.Response.BodyWriter.FlushAsync(ct);
                http.Response.Body = capture.Inner;
            }

            var record = await session.LoadAsync<Models.IdempotencyRecord>(scopedKey, ct);
            if (record is not null)
            {
                record.Completed = true;

                // Only a replayable claim had its response copied. Anonymous callers and routes that
                // return a credential keep the key and nothing else.
                if (record.Replayable && capture is not null)
                {
                    record.StatusCode = status;
                    record.ContentType = http.Response.ContentType;
                    record.Location = http.Response.Headers.Location.Count > 0
                        ? http.Response.Headers.Location.ToString()
                        : null;

                    if (capture.Overflowed)
                    {
                        record.ResponseTooLarge = true;
                    }
                    else
                    {
                        // Encrypted with the stored-secret key, never kept as plain text. Without a
                        // protector nothing is stored, and a retry gets the 409 it got before.
                        record.ProtectedResponseBody = IdempotencyFilter.Protect(http, capture.Captured);
                    }
                }

                session.Store(record);
                await session.SaveChangesAsync(ct);
            }
        }
        else
        {
            if (capture is not null) http.Response.Body = capture.Inner;

            // The request did not succeed, so release the key and let a retry run.
            session.Delete<Models.IdempotencyRecord>(scopedKey);
            await session.SaveChangesAsync(ct);
            logger?.LogDebug("Released idempotency key after a failed request: {Key}", scopedKey);
        }
    }

    /// <summary>
    /// Whether the claim is kept. Only a 2xx is: a 3xx, like a 4xx or 5xx, releases the key so the
    /// client can retry.
    /// </summary>
    internal static bool Succeeded(int status, bool exceptionOccurred, bool validationFailed) =>
        !exceptionOccurred && !validationFailed && status is >= 200 and < 300;
}
