namespace barakoCMS.Models;

/// <summary>
/// A tenant in a multi-tenant deployment: what routing and administration need. A global document
/// (not itself tenant-scoped). Single-tenant deployments run under <see cref="DefaultSlug"/>.
/// </summary>
/// <remarks>
/// The public profile a tenant used to carry here lives in the tenant's <c>site</c> entry, where it
/// grows by adding a field instead of a property. The obsolete members below are still read from a
/// document stored before <c>migrations/4.6.0/tenant-profile-to-site.sql</c> moved its values. The
/// core no longer sets them, and blanks one only when a tenant update asks it to.
/// </remarks>
public class Tenant
{
    public Guid Id { get; set; }

    /// <summary>URL-safe public identifier / handle, used to route to the tenant (the path segment).</summary>
    public string Slug { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    [Obsolete("Moved to the Logo field of the tenant's site entry. Removal planned for barakoCMS 6.0.")]
    public string? LogoUrl { get; set; }

    [Obsolete("Moved to the About field of the tenant's site entry. Removal planned for barakoCMS 6.0.")]
    public string? About { get; set; }

    /// <summary>Human-readable location label, e.g. a city.</summary>
    [Obsolete("Moved to the Location field of the tenant's site entry. Removal planned for barakoCMS 6.0.")]
    public string? Location { get; set; }

    /// <summary>
    /// Optional absolute URL that opens the location in a maps app. Validated as http or https on
    /// write by releases before 4.6.0, which is the only time it was written.
    /// </summary>
    [Obsolete("Moved to the LocationUrl field of the tenant's site entry. Removal planned for barakoCMS 6.0.")]
    public string? LocationUrl { get; set; }

    /// <summary>A social handle, e.g. "@acmeclub".</summary>
    [Obsolete("Moved to the SocialHandle field of the tenant's site entry. Removal planned for barakoCMS 6.0.")]
    public string? SocialHandle { get; set; }

    [Obsolete("Moved to the Email field of the tenant's site entry. Removal planned for barakoCMS 6.0.")]
    public string? Email { get; set; }

    /// <summary>
    /// Absolute contact URL. Validated as http or https on write by releases before 4.6.0, which is
    /// the only time it was written.
    /// </summary>
    [Obsolete("Moved to the ContactUrl field of the tenant's site entry. Removal planned for barakoCMS 6.0.")]
    public string? ContactUrl { get; set; }

    /// <summary>Freeform branding (theme color, etc.) resolved by the host/UI.</summary>
    [Obsolete("A site's colours and theme belong in the tenant's site entry. Removal planned for barakoCMS 6.0.")]
    public Dictionary<string, string> Branding { get; set; } = new();

    /// <summary>
    /// Domains this tenant answers on, so a client can use their own address rather than a
    /// subdomain. A domain belongs to exactly one tenant.
    /// </summary>
    /// <remarks>
    /// Store the bare host, e.g. "abc.com". A leading "www." is ignored on both sides of the match,
    /// so listing it separately is unnecessary and listing only "www.abc.com" still matches
    /// "abc.com". Empty for a tenant reached by subdomain or by the X-Tenant header.
    /// </remarks>
    public List<string> Domains { get; set; } = new();

    /// <summary>
    /// Whether the tenant is served. Read once, when a request is resolved to this tenant, and by
    /// every background pass that visits tenants.
    /// </summary>
    public TenantStatus Status { get; set; } = TenantStatus.Active;

    /// <summary>True when <see cref="Status"/> is <see cref="TenantStatus.Active"/>.</summary>
    /// <remarks>
    /// Still stored, as a copy of <see cref="Status"/>, so a document written by this release reads
    /// correctly in a release that knows only this flag, and one written by such a release reads
    /// correctly here: with no <c>Status</c> in it, false comes back as Suspended. Setting false
    /// leaves an Archived tenant Archived, so the order the two are read in does not matter.
    /// </remarks>
    [Obsolete("Read Status. Removal planned for barakoCMS 6.0.")]
    public bool IsActive
    {
        get => Status == TenantStatus.Active;
        set
        {
            if (value)
            {
                Status = TenantStatus.Active;
            }
            else if (Status == TenantStatus.Active)
            {
                Status = TenantStatus.Suspended;
            }
        }
    }

    /// <summary>Who may join this tenant by signing themselves up. <see cref="TenantSignUp.NotSet"/> keeps the behaviour from before the policy existed.</summary>
    public TenantSignUp SignUp { get; set; } = TenantSignUp.NotSet;

    /// <summary>The role a person who signs up under <see cref="TenantSignUp.Open"/> is given in this tenant.</summary>
    public Guid? SignUpRoleId { get; set; }

    /// <summary>How members of this tenant sign in: a second factor, the methods allowed, the session length.</summary>
    public TenantSignInPolicy SignIn { get; set; } = new();

    /// <summary>
    /// The <see cref="TenantAccount"/> that owns this tenant. Null means the implicit default account, which
    /// is what every tenant stored before accounts existed has.
    /// </summary>
    public Guid? AccountId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The implicit tenant that existing single-tenant data and non-handle requests use.</summary>
    public const string DefaultSlug = "default";
}

/// <summary>Whether a tenant is served.</summary>
/// <remarks>Stored as its number, so the values are fixed.</remarks>
public enum TenantStatus
{
    /// <summary>Served: delivery, authoring, sign in and scheduled work.</summary>
    Active = 0,

    /// <summary>
    /// Switched off for now. Delivery answers 404, authoring 403, no token or key is honoured, and
    /// scheduled work waits until the tenant is Active again.
    /// </summary>
    Suspended = 1,

    /// <summary>Refused exactly as Suspended is, kept apart so an operator can say the data is kept and nothing more.</summary>
    Archived = 2,
}

/// <summary>Who may join a tenant through registration, social sign-in or an invitation.</summary>
/// <remarks>Stored as its number, so the values are fixed.</remarks>
public enum TenantSignUp
{
    /// <summary>
    /// What every tenant did before the policy existed: registration and social sign-in create an
    /// account with no membership here, and an administrator adds members.
    /// </summary>
    NotSet = 0,

    /// <summary>Nobody signs themselves up, and an invitation cannot create an account. An existing account can still be added.</summary>
    Closed = 1,

    /// <summary>Nobody signs themselves up. An administrator's invitation is the way in.</summary>
    InviteOnly = 2,

    /// <summary>Anyone who signs up joins as a member with <see cref="Tenant.SignUpRoleId"/>.</summary>
    Open = 3,
}

/// <summary>
/// A tenant's rules for signing in. Each is checked when a token for the tenant is issued, on top of
/// what the deployment and the member's roles already require, so the strictest applies. The
/// defaults are what a tenant did before the policy existed.
/// </summary>
public class TenantSignInPolicy
{
    public const string Password = "password";
    public const string Otp = "otp";
    public const string Social = "social";

    /// <summary>The methods <see cref="Methods"/> may name.</summary>
    public static readonly IReadOnlyList<string> KnownMethods = [Password, Otp, Social];

    /// <summary>Every member needs a second factor here, whatever their roles.</summary>
    public bool RequireMfa { get; set; }

    /// <summary>A member holding one of these roles needs a second factor here, as if the role required it.</summary>
    public List<Guid> MfaRoleIds { get; set; } = new();

    /// <summary>
    /// The ways to start a sign-in here. Empty allows every built-in method. A method a module adds
    /// through <c>ISignInMethod</c> is allowed only when named here.
    /// </summary>
    public List<string> Methods { get; set; } = new();

    /// <summary>
    /// The longest a refresh token minted for this tenant lives, in minutes, when shorter than the
    /// deployment's seven days. An access token is cut to it too when it is shorter than fifteen
    /// minutes. Null is the deployment's length.
    /// </summary>
    public int? SessionMinutes { get; set; }

    /// <summary>Whether <paramref name="method"/> may start a sign-in here.</summary>
    public bool Allows(string method) =>
        Methods.Count == 0 || Methods.Contains(method, StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="method"/> is named in <see cref="Methods"/>. An empty list names nothing.</summary>
    public bool AllowsExplicitly(string method) =>
        Methods.Contains(method, StringComparer.OrdinalIgnoreCase);
}
