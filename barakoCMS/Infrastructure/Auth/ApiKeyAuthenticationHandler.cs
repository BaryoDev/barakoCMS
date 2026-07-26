using System.Security.Claims;
using System.Text.Encodings.Web;
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
        if (key is null || key.Revoked)
            return Fail();
        if (key.ExpiresAt is { } exp && exp < DateTime.UtcNow)
            return Fail();

        var user = await lookup.LoadAsync<User>(key.UserId, ct);
        if (user is null)
            return Fail();

        var slug = (key.TenantSlug ?? Tenant.DefaultSlug).Trim().ToLowerInvariant();

        // A key must not outlive its owner's access to the tenant — mirror the token-issuer membership
        // check, so revoking a membership disables the key on its next request, not at expiry.
        if (await IsDeniedForTenant(lookup, user, slug, ct))
            return Fail();

        // Scope the request now, before any content session is opened.
        Context.RequestServices.GetRequiredService<TenantContext>().Slug = slug;

        var roleIds = await MembershipRoles.EffectiveRoleIdsAsync(lookup, user, slug, ct);
        var roleNames = await lookup.Query<Role>()
            .Where(r => roleIds.Contains(r.Id))
            .Select(r => r.Name)
            .ToListAsync(ct);

        var claims = new List<Claim>
        {
            new("UserId", user.Id.ToString()),
            new("Username", user.Username),
            new("tenant", slug),
            new("auth_method", "apikey"),
            new("apikey_id", key.Id.ToString()),
        };
        foreach (var role in roleNames)
            claims.Add(new Claim(ClaimTypes.Role, role));
        if (roleNames.Count == 0)
            claims.Add(new Claim(ClaimTypes.Role, "User")); // same fallback the JWT issuer uses
        foreach (var scope in key.Scopes)
            claims.Add(new Claim("scope", scope));

        _ = TouchLastUsedAsync(store, key.Id, key.LastUsedAt); // best-effort, non-blocking

        var identity = new ClaimsIdentity(claims, SchemeName, "Username", ClaimTypes.Role);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return AuthenticateResult.Success(ticket);
    }

    private static AuthenticateResult Fail() => AuthenticateResult.Fail("Invalid API key");

    // Same logic as TokenIssuer.CheckTenantAccessAsync: default + unregistered slugs are allowed,
    // an inactive tenant or a missing active membership are refused.
    private static async Task<bool> IsDeniedForTenant(IQuerySession session, User user, string slug, CancellationToken ct)
    {
        if (slug == Tenant.DefaultSlug) return false;
        var tenant = await session.Query<Tenant>().FirstOrDefaultAsync(t => t.Slug == slug, ct);
        if (tenant is null) return false;
        if (!tenant.IsActive) return true;
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
