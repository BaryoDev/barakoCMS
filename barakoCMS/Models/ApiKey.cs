namespace barakoCMS.Models;

/// <summary>
/// A long-lived credential for machine callers (the TypeScript SDK, CI, integrations) so they don't
/// have to hold a human's username/password and mint short JWTs. The secret is shown once at creation;
/// only its SHA-256 hash is stored, so a database leak never reveals a usable key. A key is scoped to
/// one tenant and acts as one owner user, with a set of scopes limiting it to the content surface.
/// Global (single-tenanted) like the other auth/identity documents.
/// </summary>
public class ApiKey
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Human label, e.g. "CI deploy" — for the owner to tell their keys apart.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>SHA-256 (hex) of the full secret. The secret itself is never stored.</summary>
    public string KeyHash { get; set; } = string.Empty;

    /// <summary>First chars of the key (e.g. <c>bcms_ab12cd34</c>) — safe to display so a key is
    /// identifiable in a list without revealing it.</summary>
    public string Prefix { get; set; } = string.Empty;

    /// <summary>The user this key authenticates as; its effective roles decide what the key can touch.</summary>
    public Guid UserId { get; set; }

    /// <summary>The single tenant this key operates in.</summary>
    public string TenantSlug { get; set; } = Tenant.DefaultSlug;

    /// <summary>What the key may do — see <see cref="ApiKeyScopes"/>. Limited to the content surface.</summary>
    public List<string> Scopes { get; set; } = new();

    /// <summary>Optional expiry. Null means it never expires (until revoked).</summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>Best-effort last-use timestamp (throttled, not written on every request).</summary>
    public DateTime? LastUsedAt { get; set; }

    /// <summary>Revoked keys are refused immediately — revocation does not wait for expiry.</summary>
    public bool Revoked { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// The scopes an API key can hold, and the rule for whether a key satisfies a required scope. Kept
/// deliberately coarse and content-only for v1: API keys are for reading/authoring content, never for
/// managing the platform (users, roles, tenants, other keys) — that stays behind human JWTs.
/// </summary>
public static class ApiKeyScopes
{
    public const string All = "*";
    public const string ContentRead = "content:read";
    public const string ContentWrite = "content:write";
    public const string ContentTypeRead = "contenttype:read";
    public const string ContentTypeWrite = "contenttype:write";

    public static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        All, ContentRead, ContentWrite, ContentTypeRead, ContentTypeWrite,
    };

    public static bool IsKnown(string scope) => !string.IsNullOrWhiteSpace(scope) && Known.Contains(scope);

    /// <summary>Does a key's granted scope set satisfy a required scope? <c>*</c> satisfies everything.</summary>
    public static bool Satisfies(IEnumerable<string> granted, string required) =>
        granted.Any(s => s == All || string.Equals(s, required, StringComparison.OrdinalIgnoreCase));
}
