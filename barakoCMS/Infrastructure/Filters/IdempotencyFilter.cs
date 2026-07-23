using FastEndpoints;
using Marten;
using Microsoft.Extensions.Logging;

namespace barakoCMS.Infrastructure.Filters;

/// <summary>
/// Claims an idempotency key <em>before</em> the handler runs so a concurrent duplicate is rejected,
/// then <see cref="IdempotencyFinalizer"/> either completes or releases the claim after.
///
/// <para>
/// The key is stored as "in progress". A request that <b>fails</b> has its claim deleted by the
/// finalizer, so a legitimate retry runs — the previous filter committed the key up front and never
/// released it, so any failed request (a validation error, a business rejection) permanently answered
/// the retry with 409. Only a request that <b>succeeds</b> keeps the key, which is what makes a
/// genuine duplicate return 409.
/// </para>
/// </summary>
public class IdempotencyFilter : IGlobalPreProcessor
{
    /// <summary>Key under which the pre-processor hands the scoped key to the finalizer.</summary>
    public const string ScopedKeyItem = "__idempotency_scoped_key";

    // An "in progress" record older than this is treated as orphaned — the process died between
    // claiming the key and finalizing it — and may be reclaimed by a retry. Comfortably longer than
    // any real request, short enough that a retry after a crash eventually succeeds.
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

    public async Task PreProcessAsync(IPreProcessorContext context, CancellationToken ct)
    {
        var http = context.HttpContext;
        var method = http.Request.Method;
        if (method != "POST" && method != "PUT" && method != "PATCH")
            return;

        if (!http.Request.Headers.TryGetValue("Idempotency-Key", out var rawKey) ||
            string.IsNullOrWhiteSpace(rawKey))
            return;

        var store = http.RequestServices.GetService<IDocumentStore>();
        if (store is null)
            return; // Marten not available — treat as no-op rather than blocking the request.

        var logger = http.RequestServices.GetService<ILogger<IdempotencyFilter>>();
        var scopedKey = IdempotencyKeyScope.Build(http, rawKey.ToString());

        // A dedicated session, decoupled from the handler's transaction, so committing the claim
        // here and releasing it in the finalizer never entangles with the handler's own writes (or
        // its failure). IdempotencyRecord is SingleTenanted, so it lands in the default partition
        // regardless of the request's tenant.
        await using var session = store.LightweightSession();

        var existing = await session.LoadAsync<Models.IdempotencyRecord>(scopedKey, ct);
        if (existing is not null)
        {
            var orphaned = !existing.Completed && DateTime.UtcNow - existing.CreatedAt > StaleAfter;
            if (!orphaned)
            {
                logger?.LogWarning("Duplicate idempotency key: {Key}", scopedKey);
                await RejectAsync(context, ct);
                return;
            }

            // Reclaim an orphaned in-progress record left by a crashed request.
            session.Delete(existing);
            await session.SaveChangesAsync(ct);
        }

        try
        {
            // The unique identity insert is the concurrency guard: two simultaneous requests with the
            // same key race here, and exactly one wins.
            session.Insert(new Models.IdempotencyRecord { Key = scopedKey, Completed = false });
            await session.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (IsUniqueViolation(ex))
        {
            logger?.LogWarning("Concurrent duplicate idempotency key: {Key}", scopedKey);
            await RejectAsync(context, ct);
            return;
        }

        // Hand the claim to the finalizer, which completes it on success or deletes it on failure.
        http.Items[ScopedKeyItem] = scopedKey;
    }

    private static async Task RejectAsync(IPreProcessorContext context, CancellationToken ct)
    {
        var http = context.HttpContext;
        http.Response.StatusCode = 409;
        // Writing the body starts the response, which is what makes FastEndpoints skip the handler.
        // Setting the status alone does not short-circuit; the endpoint would still run (and succeed),
        // defeating the duplicate check.
        await http.Response.WriteAsync("Request with this Idempotency-Key already processed.", ct);
    }

    internal static bool IsUniqueViolation(Exception ex) =>
        ex.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("unique", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("23505") ||
        ex.InnerException?.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) == true ||
        ex.InnerException?.Message.Contains("23505") == true;
}
