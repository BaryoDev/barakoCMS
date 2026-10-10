using System.Text;
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
/// the finalizer, so a legitimate retry runs. A request that does keeps the key, and, when the caller
/// is authenticated and the route does not return a credential, its response, so a retry of the
/// same request from the same caller gets that response back.
/// </para>
/// </summary>
public class IdempotencyFilter : IGlobalPreProcessor
{
    /// <summary>Key under which the pre-processor hands the scoped key to the finalizer.</summary>
    public const string ScopedKeyItem = "__idempotency_scoped_key";

    internal const string CaptureItem = "__idempotency_capture";

    internal const string ClaimIdItem = "__idempotency_claim_id";

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
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

    internal static bool IsKeyedWrite(HttpRequest request) =>
        (HttpMethods.IsPost(request.Method) || HttpMethods.IsPut(request.Method) || HttpMethods.IsPatch(request.Method))
        && request.Headers.TryGetValue("Idempotency-Key", out var rawKey)
        && !string.IsNullOrWhiteSpace(rawKey);

    public async Task PreProcessAsync(IPreProcessorContext context, CancellationToken ct)
    {
        var http = context.HttpContext;
        if (!IsKeyedWrite(http.Request))
        {
            return;
        }

        var store = http.RequestServices.GetService<IDocumentStore>();
        if (store is null)
        {
            return; // Marten not available, so treat as no-op rather than blocking the request.
        }

        var options = http.RequestServices.GetService<IdempotencyOptions>() ?? new IdempotencyOptions();
        var now = (http.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        var logger = http.RequestServices.GetService<ILogger<IdempotencyFilter>>();
        var scopedKey = IdempotencyKeyScope.Build(http, http.Request.Headers["Idempotency-Key"].ToString());

        // Every unauthenticated caller shares one key bucket, so a stored response there could be
        // replayed to a stranger who guessed or saw the key. Those callers keep dedupe only: a retry
        // of the same request is a 409 and no response is stored. A route that never replays keeps
        // no request hash either, because its body can hold a password.
        var protector = http.RequestServices.GetService<IdempotencyProtector>();
        var anonymous = IdempotencyKeyScope.Caller(http) is null;
        var neverReplays = NeverReplays(http);
        var replayable = !anonymous && !neverReplays && protector is not null;

        var method = http.Request.Method;
        var path = http.Request.Path.Value ?? "";
        var requestHash = neverReplays || protector is null ? null : await HashRequestAsync(protector, http.Request, ct);

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
                await AnswerExistingAsync(http, existing, anonymous, method, path, requestHash, logger, scopedKey, ct);
                return;
            }
        }

        var claim = new Models.IdempotencyRecord
        {
            Key = scopedKey,
            Completed = false,
            CreatedAt = now,
            ClaimId = Guid.NewGuid(),
            Method = method,
            Path = path,
            RequestHash = requestHash,
            Replayable = replayable,
        };

        if (!await TryClaimAsync(session, claim, replacing: existing is not null, options.KeyLifetime, ct))
        {
            logger?.LogWarning("Concurrent duplicate idempotency key: {Key}", barakoCMS.Infrastructure.Logging.LogSafe.Value(scopedKey));
            await WriteTextAsync(http, 409, InProgressMessage, ct);
            return;
        }

        // Hand the claim to the finalizer, which completes it on success or deletes it on failure,
        // and copy the response as it is written when a retry may be answered with it.
        http.Items[ScopedKeyItem] = scopedKey;
        http.Items[ClaimIdItem] = claim.ClaimId;
        if (replayable)
        {
            var capture = new IdempotencyResponseCapture(http.Response.Body, options.MaxStoredResponseBytes);
            http.Response.Body = capture;
            http.Items[CaptureItem] = capture;
        }
    }

    /// <summary>
    /// Inserts the claim, first removing an expired or orphaned record under the same key when
    /// <paramref name="replacing"/>, in one transaction.
    /// </summary>
    /// <returns>False when another request holds the key, so this one must not run.</returns>
    /// <remarks>
    /// The delete repeats the expiry test in its WHERE rather than deleting by id. Two retries that
    /// both found the same expired record race here: the first replaces it with a fresh claim, and
    /// the second's delete then matches nothing, so its insert hits the unique key and it backs off.
    /// A plain delete by id would remove the first one's fresh claim and let both handlers run.
    /// </remarks>
    internal static async Task<bool> TryClaimAsync(
        IDocumentSession session, Models.IdempotencyRecord claim, bool replacing, TimeSpan keyLifetime, CancellationToken ct)
    {
        if (replacing)
        {
            var key = claim.Key;
            var expiredBefore = claim.CreatedAt - keyLifetime;
            var staleBefore = claim.CreatedAt - StaleAfter;
            session.DeleteWhere<Models.IdempotencyRecord>(r =>
                r.Key == key
                && (r.CreatedAt <= expiredBefore || (!r.Completed && r.CreatedAt < staleBefore)));
        }

        try
        {
            // The unique identity insert is the concurrency guard: two simultaneous requests with the
            // same key race here, and exactly one wins.
            session.Insert(claim);
            await session.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception ex) when (IsUniqueViolation(ex))
        {
            return false;
        }
    }

    private static bool NeverReplays(HttpContext http)
    {
        var endpoint = http.GetEndpoint();
        if (endpoint is null)
        {
            return false;
        }

        if (endpoint.Metadata.GetMetadata<NoIdempotentReplayAttribute>() is not null)
        {
            return true;
        }

        var definition = endpoint.Metadata.GetMetadata<EndpointDefinition>();
        return definition?.EndpointType.IsDefined(typeof(NoIdempotentReplayAttribute), inherit: true) == true;
    }

    private static async Task AnswerExistingAsync(
        HttpContext http,
        Models.IdempotencyRecord existing,
        bool anonymous,
        string method,
        string path,
        string? requestHash,
        ILogger? logger,
        string scopedKey,
        CancellationToken ct)
    {
        if (!existing.Completed)
        {
            logger?.LogWarning("Idempotency key still in progress: {Key}", barakoCMS.Infrastructure.Logging.LogSafe.Value(scopedKey));
            await WriteTextAsync(http, 409, InProgressMessage, ct);
            return;
        }

        // Completed before requests were recorded: nothing to compare and nothing to replay.
        if (existing.Method is null)
        {
            await WriteTextAsync(http, 409, AlreadyProcessedMessage, ct);
            return;
        }

        // Compared for every caller, anonymous ones included, so somebody who learns a key cannot use
        // it first with a different request and have the real one told it already succeeded. The hash
        // is compared only when the claim kept one.
        if (!string.Equals(existing.Method, method, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(existing.Path, path, StringComparison.Ordinal)
            || (existing.RequestHash is not null
                && !string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal)))
        {
            logger?.LogWarning("Idempotency key reused for a different request: {Key}", barakoCMS.Infrastructure.Logging.LogSafe.Value(scopedKey));
            await WriteTextAsync(http, 422, DifferentRequestMessage, ct);
            return;
        }

        if (anonymous || !existing.Replayable)
        {
            await WriteTextAsync(http, 409, AlreadyProcessedMessage, ct);
            return;
        }

        if (existing.ResponseTooLarge)
        {
            await WriteTextAsync(http, 409, TooLargeMessage, ct);
            return;
        }

        var body = existing.StatusCode is not null && existing.ProtectedResponseBody is not null
            ? http.RequestServices.GetService<IdempotencyProtector>()?.Open(existing.ProtectedResponseBody, existing.Key)
            : null;
        if (body is null)
        {
            // Nothing stored, or a body that does not open for this record: the key was rotated, or
            // the value was not sealed for this key.
            await WriteTextAsync(http, 409, AlreadyProcessedMessage, ct);
            return;
        }

        logger?.LogInformation("Replaying the stored response for idempotency key: {Key}", barakoCMS.Infrastructure.Logging.LogSafe.Value(scopedKey));

        http.Response.StatusCode = existing.StatusCode!.Value;
        if (existing.ContentType is not null)
        {
            http.Response.ContentType = existing.ContentType;
        }
        if (existing.Location is not null)
        {
            http.Response.Headers.Location = existing.Location;
        }
        http.Response.Headers[ReplayedHeader] = "true";
        http.Response.ContentLength = body.Length;

        // Starting the response is what makes FastEndpoints skip the handler. Setting the status
        // alone does not short-circuit, and an empty body would not start it either.
        await http.Response.StartAsync(ct);
        if (body.Length > 0)
        {
            await http.Response.Body.WriteAsync(body, ct);
        }
    }

    /// <summary>Hex HMAC-SHA256 over the query string and the body.</summary>
    /// <remarks>
    /// <see cref="IdempotencyRequestBuffering"/> makes the body rewindable. A host that composes its
    /// own pipeline without it still gets deduplication and replay, with only the query string
    /// compared.
    /// </remarks>
    internal static async Task<string> HashRequestAsync(IdempotencyProtector protector, HttpRequest request, CancellationToken ct)
    {
        using var hash = protector.CreateRequestHash();
        hash.AppendData(Encoding.UTF8.GetBytes(request.QueryString.Value ?? ""));
        hash.AppendData([0]);

        if (request.Body.CanSeek)
        {
            var position = request.Body.Position;
            request.Body.Position = 0;
            var buffer = new byte[81920];
            int read;
            while ((read = await request.Body.ReadAsync(buffer, ct)) > 0)
            {
                hash.AppendData(buffer, 0, read);
            }
            request.Body.Position = position;
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static async Task WriteTextAsync(HttpContext http, int status, string message, CancellationToken ct)
    {
        http.Response.StatusCode = status;
        http.Response.ContentType = "text/plain; charset=utf-8";
        // Writing the body starts the response, which is what makes FastEndpoints skip the handler.
        await http.Response.WriteAsync(message, ct);
    }

    internal static bool IsUniqueViolation(Exception ex) =>
        // Marten reports a second insert of one id as its own exception, not as the 23505 under it.
        ex is JasperFx.DocumentAlreadyExistsException ||
        ex.InnerException is JasperFx.DocumentAlreadyExistsException ||
        ex is Npgsql.PostgresException { SqlState: "23505" } ||
        ex.InnerException is Npgsql.PostgresException { SqlState: "23505" } ||
        ex.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("unique", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("23505") ||
        ex.InnerException?.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) == true ||
        ex.InnerException?.Message.Contains("23505") == true;
}
