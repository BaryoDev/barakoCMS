using System.Security.Cryptography;
using FastEndpoints;
using Marten;
using Microsoft.Extensions.Logging;

namespace barakoCMS.Infrastructure.Filters;

/// <summary>
/// Claims an idempotency key <em>before</em> the handler runs so a concurrent duplicate is rejected,
/// then <see cref="IdempotencyFinalizer"/> either completes or releases the claim after.
///
/// <para>
/// The key is stored as "in progress". A request that does not answer 2xx has its claim deleted by
/// the finalizer, so a legitimate retry runs. A request that does keeps the key along with its
/// response, and a retry of the same request from the same caller gets that response back.
/// </para>
/// </summary>
public class IdempotencyFilter : IGlobalPreProcessor
{
    /// <summary>Key under which the pre-processor hands the scoped key to the finalizer.</summary>
    public const string ScopedKeyItem = "__idempotency_scoped_key";

    internal const string CaptureItem = "__idempotency_capture";

    public const string ReplayedHeader = "Idempotent-Replayed";

    internal const string InProgressMessage =
        "A request with this Idempotency-Key is still in progress.";

    internal const string AlreadyProcessedMessage =
        "Request with this Idempotency-Key already processed.";

    internal const string TooLargeMessage =
        "Request with this Idempotency-Key already processed. Its response was too large to keep, so it cannot be replayed.";

    internal const string DifferentRequestMessage =
        "This Idempotency-Key was already used for a different request.";

    // An "in progress" record older than this is treated as orphaned (the process died between
    // claiming the key and finalizing it) and may be reclaimed by a retry. Comfortably longer than
    // any real request, short enough that a retry after a crash eventually succeeds.
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

    internal static bool IsKeyedWrite(HttpRequest request) =>
        (HttpMethods.IsPost(request.Method) || HttpMethods.IsPut(request.Method) || HttpMethods.IsPatch(request.Method))
        && request.Headers.TryGetValue("Idempotency-Key", out var rawKey)
        && !string.IsNullOrWhiteSpace(rawKey);

    public async Task PreProcessAsync(IPreProcessorContext context, CancellationToken ct)
    {
        var http = context.HttpContext;
        if (!IsKeyedWrite(http.Request))
            return;

        var store = http.RequestServices.GetService<IDocumentStore>();
        if (store is null)
            return; // Marten not available, so treat as no-op rather than blocking the request.

        var options = http.RequestServices.GetService<IdempotencyOptions>() ?? new IdempotencyOptions();
        var now = (http.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        var logger = http.RequestServices.GetService<ILogger<IdempotencyFilter>>();
        var scopedKey = IdempotencyKeyScope.Build(http, http.Request.Headers["Idempotency-Key"].ToString());

        var method = http.Request.Method;
        var path = http.Request.Path.Value ?? "";
        var bodyHash = await HashBodyAsync(http.Request, ct);

        // A dedicated session, decoupled from the handler's transaction, so committing the claim
        // here and releasing it in the finalizer never entangles with the handler's own writes (or
        // its failure). IdempotencyRecord is SingleTenanted, so it lands in the default partition
        // regardless of the request's tenant.
        await using var session = store.LightweightSession();

        var existing = await session.LoadAsync<Models.IdempotencyRecord>(scopedKey, ct);
        if (existing is not null)
        {
            var expired = now - existing.CreatedAt >= options.KeyLifetime;
            var orphaned = !existing.Completed && now - existing.CreatedAt > StaleAfter;

            if (!expired && !orphaned)
            {
                await AnswerExistingAsync(http, existing, method, path, bodyHash, logger, scopedKey, ct);
                return;
            }

            // An expired key is free again, and an orphaned claim was left by a crashed request.
            session.Delete(existing);
            await session.SaveChangesAsync(ct);
        }

        try
        {
            // The unique identity insert is the concurrency guard: two simultaneous requests with the
            // same key race here, and exactly one wins.
            session.Insert(new Models.IdempotencyRecord
            {
                Key = scopedKey,
                Completed = false,
                CreatedAt = now,
                Method = method,
                Path = path,
                BodyHash = bodyHash,
            });
            await session.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (IsUniqueViolation(ex))
        {
            logger?.LogWarning("Concurrent duplicate idempotency key: {Key}", scopedKey);
            await WriteTextAsync(http, 409, InProgressMessage, ct);
            return;
        }

        // Hand the claim to the finalizer, which completes it on success or deletes it on failure,
        // and copy the response as it is written so a retry can be answered with it.
        http.Items[ScopedKeyItem] = scopedKey;
        var capture = new IdempotencyResponseCapture(http.Response.Body, options.MaxStoredResponseBytes);
        http.Response.Body = capture;
        http.Items[CaptureItem] = capture;
    }

    private static async Task AnswerExistingAsync(
        HttpContext http,
        Models.IdempotencyRecord existing,
        string method,
        string path,
        string? bodyHash,
        ILogger? logger,
        string scopedKey,
        CancellationToken ct)
    {
        if (!existing.Completed)
        {
            logger?.LogWarning("Idempotency key still in progress: {Key}", scopedKey);
            await WriteTextAsync(http, 409, InProgressMessage, ct);
            return;
        }

        if (existing.Method is null)
        {
            // Completed before responses were kept, so there is nothing to replay.
            await WriteTextAsync(http, 409, AlreadyProcessedMessage, ct);
            return;
        }

        if (!string.Equals(existing.Method, method, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(existing.Path, path, StringComparison.Ordinal)
            || !string.Equals(existing.BodyHash, bodyHash, StringComparison.Ordinal))
        {
            logger?.LogWarning("Idempotency key reused for a different request: {Key}", scopedKey);
            await WriteTextAsync(http, 422, DifferentRequestMessage, ct);
            return;
        }

        if (existing.ResponseTooLarge || existing.StatusCode is null)
        {
            await WriteTextAsync(http, 409, TooLargeMessage, ct);
            return;
        }

        logger?.LogInformation("Replaying the stored response for idempotency key: {Key}", scopedKey);

        var body = existing.ResponseBody ?? [];
        http.Response.StatusCode = existing.StatusCode.Value;
        if (existing.ContentType is not null) http.Response.ContentType = existing.ContentType;
        if (existing.Location is not null) http.Response.Headers.Location = existing.Location;
        http.Response.Headers[ReplayedHeader] = "true";
        http.Response.ContentLength = body.Length;

        // Starting the response is what makes FastEndpoints skip the handler. Setting the status
        // alone does not short-circuit, and an empty body would not start it either.
        await http.Response.StartAsync(ct);
        if (body.Length > 0) await http.Response.Body.WriteAsync(body, ct);
    }

    /// <summary>Hex SHA-256 of the request body, or null when the body cannot be rewound to read.</summary>
    /// <remarks>
    /// <see cref="IdempotencyRequestBuffering"/> makes it rewindable. A host that composes its own
    /// pipeline without it still gets deduplication and replay, only not the different-body check.
    /// </remarks>
    private static async Task<string?> HashBodyAsync(HttpRequest request, CancellationToken ct)
    {
        if (!request.Body.CanSeek) return null;

        var position = request.Body.Position;
        request.Body.Position = 0;
        var hash = await SHA256.HashDataAsync(request.Body, ct);
        request.Body.Position = position;
        return Convert.ToHexString(hash);
    }

    private static async Task WriteTextAsync(HttpContext http, int status, string message, CancellationToken ct)
    {
        http.Response.StatusCode = status;
        http.Response.ContentType = "text/plain; charset=utf-8";
        // Writing the body starts the response, which is what makes FastEndpoints skip the handler.
        await http.Response.WriteAsync(message, ct);
    }

    internal static bool IsUniqueViolation(Exception ex) =>
        ex.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("unique", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("23505") ||
        ex.InnerException?.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) == true ||
        ex.InnerException?.Message.Contains("23505") == true;
}
