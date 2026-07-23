using Marten.Schema;

namespace barakoCMS.Models;

/// <summary>
/// Tracks an in-flight or completed idempotent request so a retry of the same request is not
/// executed twice.
///
/// <para>
/// <see cref="Key"/> is the <em>scoped</em> key: the client's Idempotency-Key namespaced by tenant
/// and user (see <c>IdempotencyKeyScope</c>). Two tenants — or two users in one tenant — can pick
/// the same raw key without colliding, which the old global-unique key allowed.
/// </para>
///
/// <para>
/// Lifecycle: a request inserts a record with <see cref="Completed"/> = false before the handler
/// runs (winning the race against a concurrent duplicate via the unique index), then a
/// post-processor either marks it <see cref="Completed"/> on success or <em>deletes</em> it on
/// failure. Deleting on failure is the point: a request that 400s or throws must be retryable, not
/// permanently answered with 409.
/// </para>
/// </summary>
public class IdempotencyRecord
{
    [Identity]
    public string Key { get; set; } = string.Empty;

    /// <summary>False while the request is in flight; true once it completed successfully.</summary>
    public bool Completed { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
