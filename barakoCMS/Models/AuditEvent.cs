namespace barakoCMS.Models;

/// <summary>
/// A single "who did what, when" record: an auth event or a sensitive administrative action.
/// Global (single-tenanted) like the other auth/identity documents, with the originating tenant
/// kept on <see cref="TenantSlug"/> as data — the same convention <see cref="ApiKey"/> uses.
///
/// Entries form a hash chain: <see cref="Hash"/> is computed over this entry's fields plus the
/// previous entry's hash (<see cref="PrevHash"/>), so editing or deleting a past entry breaks every
/// hash after it. This is tamper-evidence, not tamper-prevention — an attacker with direct database
/// access can still rewrite the whole chain forward from their edit. It catches accidental
/// corruption and casual tampering, which is the threat this log is meant to detect.
/// </summary>
public class AuditEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string TenantSlug { get; set; } = Tenant.DefaultSlug;

    /// <summary>Dotted action name, e.g. "auth.login.succeeded", "role.deleted".</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>Who did it. Null for actions that happen before a user is known (e.g. a failed login
    /// against a username that doesn't exist).</summary>
    public Guid? ActorUserId { get; set; }
    public string? ActorUsername { get; set; }

    /// <summary>What it was done to, if the action targets a specific record — e.g. TargetType
    /// "Role", TargetId the role's id. Both null for actions with no single target (e.g. login).</summary>
    public string? TargetType { get; set; }
    public string? TargetId { get; set; }

    /// <summary>Small, action-specific extra context (e.g. the role name that was deleted). Kept
    /// deliberately shallow — this is an audit trail, not a general event store.</summary>
    public Dictionary<string, object>? Metadata { get; set; }

    public string? IpAddress { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The previous entry's <see cref="Hash"/> in this tenant's chain, or
    /// <see cref="AuditChain.GenesisHash"/> for the first entry.</summary>
    public string PrevHash { get; set; } = string.Empty;

    /// <summary>SHA-256 (hex) over <see cref="PrevHash"/> and this entry's own fields.</summary>
    public string Hash { get; set; } = string.Empty;
}
