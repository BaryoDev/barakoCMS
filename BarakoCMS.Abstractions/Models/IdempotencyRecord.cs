using JasperFx;
using Marten.Schema;

namespace barakoCMS.Models;

/// <summary>
/// Tracks an in-flight or completed idempotent request so a retry of the same request is not
/// executed twice, and so the retry gets the first response back.
///
/// <para>
/// <see cref="Key"/> is the <em>scoped</em> key: the client's Idempotency-Key namespaced by tenant
/// and caller (see <c>IdempotencyKeyScope</c>). Two tenants, or two callers in one tenant, can pick
/// the same raw key without colliding.
/// </para>
///
/// <para>
/// Lifecycle: a request inserts a record with <see cref="Completed"/> = false before the handler
/// runs (winning the race against a concurrent duplicate via the unique index), then a
/// post-processor either marks it <see cref="Completed"/> and stores the response on a 2xx, or
/// <em>deletes</em> it otherwise, so a request that failed stays retryable.
/// </para>
/// </summary>
public class IdempotencyRecord
{
    [Identity]
    public string Key { get; set; } = string.Empty;

    /// <summary>False while the request is in flight; true once it completed successfully.</summary>
    public bool Completed { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Identifies the request holding this claim, so a request that ran long and lost its claim to a
    /// retry cannot complete or release the retry's claim.
    /// </summary>
    public Guid ClaimId { get; set; }

    /// <summary>
    /// The method of the request that claimed the key. Null on a record written before replay
    /// existed, which is answered 409 on a retry.
    /// </summary>
    public string? Method { get; set; }

    /// <summary>The path of the request that claimed the key, without its query string.</summary>
    public string? Path { get; set; }

    /// <summary>
    /// Hex HMAC-SHA256 over the query string and the body, under a key kept for this purpose, so a
    /// different request under the same key is refused. Null on a route that never replays, whose
    /// body may hold a password.
    /// </summary>
    public string? RequestHash { get; set; }

    /// <summary>
    /// False when the route never replays (it returns a credential), so a retry is answered 409 and
    /// no response is stored.
    /// </summary>
    public bool Replayable { get; set; }

    public int? StatusCode { get; set; }

    public string? ContentType { get; set; }

    public string? Location { get; set; }

    /// <summary>
    /// The response body, sealed with a key kept for this purpose and bound to <see cref="Key"/>. It
    /// is one the caller that owns the key was already sent. Never logged.
    /// </summary>
    public string? ProtectedResponseBody { get; set; }

    /// <summary>True when the response was over the stored size limit, so it cannot be replayed.</summary>
    public bool ResponseTooLarge { get; set; }
}
