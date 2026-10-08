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
    /// The method of the request that claimed the key. Null on a record written before replay
    /// existed, which is answered 409 because it holds no response.
    /// </summary>
    public string? Method { get; set; }

    /// <summary>The path of the request that claimed the key, without its query string.</summary>
    public string? Path { get; set; }

    /// <summary>Hex SHA-256 of the request body, so a different body under the same key is refused.</summary>
    public string? BodyHash { get; set; }

    public int? StatusCode { get; set; }

    public string? ContentType { get; set; }

    public string? Location { get; set; }

    /// <summary>
    /// The response body, which the caller that owns the key was already sent. Never logged.
    /// </summary>
    public byte[]? ResponseBody { get; set; }

    /// <summary>True when the response was over the stored size limit, so it cannot be replayed.</summary>
    public bool ResponseTooLarge { get; set; }
}
