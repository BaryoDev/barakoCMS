using System.Security.Cryptography;

namespace barakoCMS.Infrastructure.Multitenancy;

/// <summary>Whether a tenant has proved it controls a domain.</summary>
internal enum TenantDomainStatus
{
    /// <summary>Claimed and not yet checked. Routes nowhere.</summary>
    Pending = 0,

    /// <summary>The DNS check passed, or the domain was already in use when verification arrived.</summary>
    Verified = 1,
}

/// <summary>
/// One domain a tenant has claimed, keyed by the host in the form <see cref="TenantDomainMap.Normalise"/>
/// gives.
/// </summary>
/// <remarks>
/// The key is the whole point. A database primary key on the host means two tenants cannot hold one
/// domain even when two writes race: the second insert fails, where a read before a write (see
/// <c>TenantDomainClash</c>) lets both through. Only a verified row routes a request.
///
/// <see cref="Tenant.Domains"/> stays the list of every domain the tenant has claimed, pending or
/// verified. It was kept as it is, rather than derived from these rows, because export, seed files,
/// the tenant response and the clash check all read it, and a tenant document stored before this
/// release must still say what it claims. The two are written in one transaction.
/// </remarks>
internal sealed class TenantDomain
{
    /// <summary>The normalised host, for example <c>example.com</c>.</summary>
    public string Id { get; set; } = string.Empty;

    public string TenantSlug { get; set; } = string.Empty;

    public TenantDomainStatus Status { get; set; } = TenantDomainStatus.Pending;

    /// <summary>
    /// What the TXT record has to hold, generated here and never chosen by a caller. One per claim, so
    /// a record published for one tenant's claim proves nothing for another's.
    /// </summary>
    public string Token { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? VerifiedAt { get; set; }

    /// <summary>The label the TXT record sits under, in front of the domain.</summary>
    public const string TxtLabel = "_barako-challenge";

    /// <summary>What the TXT record has to say, in front of the token.</summary>
    public const string TxtPrefix = "barako-verify=";

    public static string NewToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));

    public static TenantDomain Claim(string host, string tenantSlug) => new()
    {
        Id = host,
        TenantSlug = tenantSlug,
        Status = TenantDomainStatus.Pending,
        Token = NewToken(),
        CreatedAt = DateTimeOffset.UtcNow,
    };

    public string TxtName() => $"{TxtLabel}.{Id}";

    public string TxtValue() => TxtPrefix + Token;
}
