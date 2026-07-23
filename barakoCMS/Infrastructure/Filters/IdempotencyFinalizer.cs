using FastEndpoints;
using Marten;
using Microsoft.Extensions.Logging;

namespace barakoCMS.Infrastructure.Filters;

/// <summary>
/// Settles the idempotency claim that <see cref="IdempotencyFilter"/> made before the handler ran:
/// marks it completed on success, or <b>deletes it on failure</b> so a legitimate retry can run.
///
/// Runs as a global post-processor, which FastEndpoints invokes even when the handler threw or
/// returned a 4xx — exactly the cases where the claim must be released.
/// </summary>
public class IdempotencyFinalizer : IGlobalPostProcessor
{
    public async Task PostProcessAsync(IPostProcessorContext context, CancellationToken ct)
    {
        var http = context.HttpContext;

        // Only requests that actually claimed a key (a fresh, non-duplicate request) get here with a
        // stashed key. A rejected duplicate never stashed one, so it can't delete the completed
        // record it collided with.
        if (!http.Items.TryGetValue(IdempotencyFilter.ScopedKeyItem, out var keyObj) ||
            keyObj is not string scopedKey)
            return;

        var store = http.RequestServices.GetService<IDocumentStore>();
        if (store is null)
            return;

        var succeeded = !context.HasExceptionOccurred
                        && !context.HasValidationFailures
                        && http.Response.StatusCode < 400;

        var logger = http.RequestServices.GetService<ILogger<IdempotencyFinalizer>>();
        await using var session = store.LightweightSession();

        if (succeeded)
        {
            // Keep the claim, marked completed, so a genuine later duplicate returns 409.
            var record = await session.LoadAsync<Models.IdempotencyRecord>(scopedKey, ct);
            if (record is not null)
            {
                record.Completed = true;
                session.Store(record);
                await session.SaveChangesAsync(ct);
            }
        }
        else
        {
            // The request failed — release the key so a retry isn't wrongly answered with 409.
            session.Delete<Models.IdempotencyRecord>(scopedKey);
            await session.SaveChangesAsync(ct);
            logger?.LogDebug("Released idempotency key after a failed request: {Key}", scopedKey);
        }
    }
}
