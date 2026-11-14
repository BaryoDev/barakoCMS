using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;

namespace barakoCMS.Features.Tenants;

/// <summary>A tenant as the API describes it, rather than as it is stored.</summary>
/// <remarks>
/// See <c>Features/Roles/RoleResponse</c> for the reasoning. This one has the sharpest version of
/// the leak argument: <see cref="Tenant"/> is a settings document that will keep growing, and every
/// property added to it would otherwise appear on the wire the moment it is stored, whether or not
/// anybody decided it should be public.
///
/// It carries what routing and administration need. The public profile is not here: it lives in the
/// tenant's <c>site</c> entry and is read from there.
/// </remarks>
internal sealed class TenantResponse
{
    public Guid Id { get; init; }
    public string Slug { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public Dictionary<string, string> Branding { get; init; } = new();
    public List<string> Domains { get; init; } = new();

    /// <summary>
    /// Where each domain stands: pending until its DNS check passes, then verified. Only a verified
    /// domain routes. A domain with no entry here has no row yet and routes nowhere either.
    /// </summary>
    public List<TenantDomainState> DomainStates { get; init; } = new();
    public TenantStatus Status { get; init; }

    /// <summary>True when <see cref="Status"/> is Active. Kept for clients written before it.</summary>
    public bool IsActive { get; init; }
    public TenantSignUp SignUp { get; init; }
    public Guid? SignUpRoleId { get; init; }
    public TenantSignInBody SignIn { get; init; } = new();
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>How many accounts hold a membership in the tenant that is not removed. Null where it was not read.</summary>
    public int? MemberCount { get; init; }

    /// <summary>
    /// The <c>Mode</c> of the tenant's published <c>site</c> entry, as stored (for example Live or
    /// Private). Null when the tenant has no site entry, or where it was not read.
    /// </summary>
    public string? SiteMode { get; init; }

    /// <summary>The account that owns the tenant. The default account's id when none was assigned.</summary>
    public Guid AccountId { get; init; }

    public static TenantResponse From(
        Tenant t, IEnumerable<TenantDomain>? claims = null, TenantListFacts? facts = null) => new()
    {
        MemberCount = facts?.MemberCount(t.Slug),
        SiteMode = facts?.SiteMode(t.Slug),
        DomainStates = (claims ?? []).OrderBy(c => c.Id, StringComparer.Ordinal).Select(TenantDomainState.From).ToList(),
        Id = t.Id,
        Slug = t.Slug,
        Name = t.Name,
#pragma warning disable CS0618 // still returned until the member goes, since nothing replaced it
        Branding = t.Branding,
#pragma warning restore CS0618
        Domains = t.Domains,
        Status = t.Status,
        IsActive = t.Status == TenantStatus.Active,
        SignUp = t.SignUp,
        SignUpRoleId = t.SignUpRoleId,
        AccountId = t.AccountId ?? TenantAccount.DefaultId,
        SignIn = TenantSignInBody.From(t.SignIn ?? new TenantSignInPolicy()),
        // Stored as DateTime, emitted with a zone, like every other timestamp this API returns.
        CreatedAt = new DateTimeOffset(DateTime.SpecifyKind(t.CreatedAt, DateTimeKind.Utc)),
    };
}

/// <summary>A claimed domain and what the tenant has to publish in DNS to verify it.</summary>
internal sealed class TenantDomainState
{
    public string Domain { get; init; } = string.Empty;
    public TenantDomainStatus Status { get; init; }

    /// <summary>The TXT record's name, <c>_barako-challenge.{domain}</c>.</summary>
    public string TxtName { get; init; } = string.Empty;

    /// <summary>What that TXT record has to hold, <c>barako-verify=</c> and a value generated for this claim.</summary>
    public string TxtValue { get; init; } = string.Empty;

    public DateTimeOffset? VerifiedAt { get; init; }

    public static TenantDomainState From(TenantDomain row) => new()
    {
        Domain = row.Id,
        Status = row.Status,
        TxtName = row.TxtName(),
        TxtValue = row.TxtValue(),
        VerifiedAt = row.VerifiedAt,
    };
}
