using System.Security.Claims;
using System.Text.Encodings.Web;
using barakoCMS.Features.Roles;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using Marten;
using Marten.Patching;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace barakoCMS.Infrastructure.Auth;

public sealed class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions;

/// <summary>
/// Authenticates <c>Authorization: Bearer bcms_...</c> API keys. Builds the SAME claims a JWT would
/// (UserId, Username, tenant, one ClaimTypes.Role per effective role with the "User" fallback) plus
/// scope claims and an <c>auth_method=apikey</c> marker, so the role gate, the permission resolver,
/// the revocation middleware and the tenant-access guard all work downstream without change.
///
/// It reads its records (ApiKey, User, Membership, Role — all global/SingleTenanted) via a throwaway
/// store session, never the request-scoped one, then sets <see cref="TenantContext"/> to the key's
/// tenant BEFORE any content session is opened — so a tenant-scoped key's reads and writes land in
/// the right partition even when the caller sends no X-Tenant header.
/// </summary>
public sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<ApiKeyAuthenticationOptions>
{
    public const string SchemeName = "ApiKey";

    /// <summary>One claim per content type a push key is limited to. Absent on an unrestricted key.</summary>
    public const string ContentTypeClaim = "apikey_content_type";

    /// <summary>The service principal id on a key that acts as one. Absent on a key that acts as a person.</summary>
    public const string PrincipalClaim = "principal_id";

    /// <summary>One claim per capability the principal's roles carry, read by <c>CallerCapabilities</c>.</summary>
    public const string PrincipalCapabilityClaim = "principal_capability";

    /// <summary>"true" on a read-only key.</summary>
    public const string ReadOnlyClaim = "apikey_readonly";

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<ApiKeyAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder) { }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        const string bearer = "Bearer ";
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith(bearer, StringComparison.Ordinal))
            return AuthenticateResult.NoResult();

        var token = header[bearer.Length..].Trim();
        if (!ApiKeyService.LooksLikeApiKey(token))
            return AuthenticateResult.NoResult(); // not ours — let another scheme try

        var store = Context.RequestServices.GetRequiredService<IDocumentStore>();
        var ct = Context.RequestAborted;

        // Global/SingleTenanted reads: a default-partition session resolves them regardless of tenant,
        // and crucially leaves the request-scoped session untouched so it opens at the key's tenant.
        await using var lookup = store.QuerySession();

        var hash = ApiKeyService.Hash(token);
        var key = await lookup.Query<ApiKey>().FirstOrDefaultAsync(k => k.KeyHash == hash, ct);

        // One generic message for every rejection — don't tell a prober whether a key exists, is
        // revoked, or is expired.
        if (key is null)
            return Fail();
        if (RefusalOf(key) is { } why)
            return Refused(key, why);

        var slug = (key.TenantSlug ?? Tenant.DefaultSlug).Trim().ToLowerInvariant();

        // The slug below replaces the one resolution checked, so the mode is checked again here: in
        // Multi a key scoped to the default partition or to a slug with no active tenant is refused,
        // as the issuer refuses a token for one.
        if (await Context.RequestServices.GetRequiredService<TenancyOptions>().RefusesAsync(lookup, slug, ct))
            return Fail();

        var claims = key.ServicePrincipalId is { } principalId
            ? await PrincipalClaimsAsync(lookup, key, principalId, slug, ct)
            : await OwnerClaimsAsync(lookup, key, slug, ct);
        if (claims is null)
            return Fail();

        // Scope the request now, before any content session is opened.
        Context.RequestServices.GetRequiredService<TenantContext>().Slug = slug;

        claims.Add(new Claim("tenant", slug));
        claims.Add(new Claim("auth_method", "apikey"));
        claims.Add(new Claim("apikey_id", key.Id.ToString()));
        if (key.ReadOnly)
            claims.Add(new Claim(ReadOnlyClaim, "true"));
        foreach (var scope in key.Scopes)
            claims.Add(new Claim("scope", scope));
        foreach (var type in key.ContentTypes ?? [])
            claims.Add(new Claim(ContentTypeClaim, type));

        _ = TouchLastUsedAsync(store, key.Id, key.LastUsedAt); // best-effort, non-blocking

        var identity = new ClaimsIdentity(claims, SchemeName, "Username", ClaimTypes.Role);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return AuthenticateResult.Success(ticket);
    }

    /// <summary>
    /// The claims of a key that acts as its owner: the owner's identity and effective roles. Null when
    /// the owner is gone or has lost access to the tenant, so a key never outlives its owner's access.
    /// </summary>
    private static async Task<List<Claim>?> OwnerClaimsAsync(IQuerySession lookup, ApiKey key, string slug, CancellationToken ct)
    {
        var user = await lookup.LoadAsync<User>(key.UserId, ct);
        if (user is null)
            return null;

        // A key must not outlive its owner's access to the tenant: mirror the token-issuer membership
        // check, so revoking a membership disables the key on its next request, not at expiry.
        if (await IsDeniedForTenant(lookup, user, slug, ct))
            return null;

        var roleIds = await MembershipRoles.EffectiveRoleIdsAsync(lookup, user, slug, ct);
        var claims = new List<Claim> { new("UserId", user.Id.ToString()), new("Username", user.Username) };
        AddRoles(claims, await RoleNamesAsync(lookup, roleIds, ct));
        return claims;
    }

    /// <summary>
    /// The claims of a key that acts as a service principal. Null when the principal is missing,
    /// disabled or belongs to another tenant than the key, or when the tenant is not active. There is
    /// no creator membership check, which is the point: the key lives on without the person who made it.
    /// The claims carry no <c>UserId</c>, so nothing treats the principal as a person; endpoints that
    /// need a real user answer 401 or 400, and the capability gate reads <see cref="PrincipalClaim"/>.
    /// </summary>
    private static async Task<List<Claim>?> PrincipalClaimsAsync(
        IQuerySession lookup, ApiKey key, Guid principalId, string slug, CancellationToken ct)
    {
        var principal = await lookup.LoadAsync<ServicePrincipal>(principalId, ct);
        if (principal is null || principal.Disabled
            || !string.Equals(principal.TenantSlug, slug, StringComparison.OrdinalIgnoreCase))
            return null;
        if (!await TenantIsActiveAsync(lookup, slug, ct))
            return null;

        var ids = principal.RoleIds.ToArray();
        var roles = await lookup.Query<Role>()
            .Where(r => r.DeletedAt == null && ids.Contains(r.Id))
            .ToListAsync(ct);
        // A role of another tenant, or a platform role, never counts, whatever the stored ids say.
        var usable = roles.Where(r => !r.IsPlatform && RoleScope.Reaches(r, slug)).ToList();

        var claims = new List<Claim>
        {
            new("Username", "principal:" + principal.Name),
            new(PrincipalClaim, principal.Id.ToString()),
        };
        AddRoles(claims, usable.Select(r => r.Name).ToList());
        foreach (var capability in usable.SelectMany(r => r.SystemCapabilities ?? []).Distinct(StringComparer.OrdinalIgnoreCase))
            claims.Add(new Claim(PrincipalCapabilityClaim, capability));
        return claims;
    }

    private static async Task<List<string>> RoleNamesAsync(IQuerySession lookup, IReadOnlyCollection<Guid> roleIds, CancellationToken ct) =>
        (await lookup.Query<Role>()
            .Where(r => r.DeletedAt == null && roleIds.Contains(r.Id))
            .Select(r => r.Name)
            .ToListAsync(ct)).ToList();

    private static void AddRoles(List<Claim> claims, List<string> roleNames)
    {
        foreach (var role in roleNames)
            claims.Add(new Claim(ClaimTypes.Role, role));
        if (roleNames.Count == 0)
            claims.Add(new Claim(ClaimTypes.Role, "User")); // same fallback the JWT issuer uses
    }

    // A tenant the store knows must be Active; the default and unregistered slugs are allowed, as for a person.
    private static async Task<bool> TenantIsActiveAsync(IQuerySession session, string slug, CancellationToken ct)
    {
        if (slug == Tenant.DefaultSlug) return true;
        var tenant = await session.Query<Tenant>().FirstOrDefaultAsync(t => t.Slug == slug, ct);
        return tenant is null || tenant.Status == TenantStatus.Active;
    }

    private static ApiKeyRefusalReason? RefusalOf(ApiKey key)
    {
        if (key.Revoked)
            return ApiKeyRefusalReason.Revoked;
        return key.ExpiresAt is { } exp && exp < DateTime.UtcNow ? ApiKeyRefusalReason.Expired : null;
    }

    // The caller still gets the one generic answer; only the admin's usage view learns the reason.
    private AuthenticateResult Refused(ApiKey key, ApiKeyRefusalReason reason)
    {
        Context.RequestServices.GetService<ApiKeyRefusals>()?.Record(key.Id, reason);
        return Fail();
    }

    private static AuthenticateResult Fail() => AuthenticateResult.Fail("Invalid API key");

    // Same logic as TokenIssuer.CheckTenantAccessAsync: default + unregistered slugs are allowed,
    // a tenant that is not Active or a missing active membership are refused.
    private static async Task<bool> IsDeniedForTenant(IQuerySession session, User user, string slug, CancellationToken ct)
    {
        if (slug == Tenant.DefaultSlug) return false;
        var tenant = await session.Query<Tenant>().FirstOrDefaultAsync(t => t.Slug == slug, ct);
        if (tenant is null) return false;
        if (tenant.Status != TenantStatus.Active) return true;
        return !await session.Query<Membership>()
            .AnyAsync(m => m.UserId == user.Id && m.TenantSlug == slug && m.Status == MembershipStatus.Active, ct);
    }

    private static async Task TouchLastUsedAsync(IDocumentStore store, Guid keyId, DateTime? lastUsed)
    {
        // Throttle to at most one write per 5 minutes so a busy key isn't a write per request.
        if (lastUsed is { } l && l > DateTime.UtcNow.AddMinutes(-5)) return;
        try
        {
            await using var s = store.LightweightSession();
            // Targeted patch of ONLY LastUsedAt. A full-document Update() here would serialize the
            // whole ApiKey and, with no optimistic concurrency, a stale copy could clobber a
            // concurrent revoke (Revoked=true) written between our load and save — silently
            // un-revoking the key. A field patch never touches Revoked, so revocation stays durable.
            s.Patch<ApiKey>(keyId).Set(x => x.LastUsedAt, DateTime.UtcNow);
            await s.SaveChangesAsync();
        }
        catch { /* last-used is best-effort; never fail a request over it */ }
    }
}
