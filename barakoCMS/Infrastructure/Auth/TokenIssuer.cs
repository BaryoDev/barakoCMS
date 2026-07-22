using System.Security.Claims;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using FastEndpoints.Security;
using Marten;
using Microsoft.IdentityModel.JsonWebTokens;

namespace barakoCMS.Infrastructure.Auth;

/// <inheritdoc cref="ITokenIssuer"/>
public sealed class TokenIssuer : ITokenIssuer
{
    private readonly IQuerySession _session;
    private readonly IConfiguration _config;
    private readonly ILogger<TokenIssuer> _logger;

    public TokenIssuer(IQuerySession session, IConfiguration config, ILogger<TokenIssuer> logger)
    {
        _session = session;
        _config = config;
        _logger = logger;
    }

    public async Task<TokenIssueResult> IssueAccessTokenAsync(
        User user,
        string tenantSlug,
        IEnumerable<Claim>? extraClaims = null,
        CancellationToken ct = default)
    {
        var slug = (tenantSlug ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(slug))
            return TokenIssueResult.Denied("no tenant supplied");

        var denial = await CheckTenantAccessAsync(user, slug, ct);
        if (denial is not null)
        {
            // Warn, not info: on a multi-tenant deployment this is someone presenting an X-Tenant
            // they have no membership for, which is worth seeing in the logs.
            _logger.LogWarning(
                "Refused to issue a token for tenant {Tenant} to user {UserId} ({Username}): {Reason}",
                slug, user.Id, user.Username, denial);
            return TokenIssueResult.Denied(denial);
        }

        var roleIds = await MembershipRoles.EffectiveRoleIdsAsync(_session, user, slug, ct);
        var roles = await _session.Query<Role>()
            .Where(r => roleIds.Contains(r.Id))
            .Select(r => r.Name)
            .ToListAsync(ct);

        var jti = Guid.NewGuid().ToString();
        var expiresAt = DateTime.UtcNow.AddMinutes(15);

        var token = JWTBearer.CreateToken(
            signingKey: _config["JWT:Key"]!,
            expireAt: expiresAt,
            issuer: _config["JWT:Issuer"],
            audience: _config["JWT:Audience"],
            privileges: u =>
            {
                u.Claims.Add(new(JwtRegisteredClaimNames.Jti, jti));
                u.Claims.Add(new("UserId", user.Id.ToString()));
                u.Claims.Add(new("Username", user.Username));
                u.Claims.Add(new("tenant", slug));

                foreach (var role in roles)
                    u.Claims.Add(new(ClaimTypes.Role, role));

                // A user with a membership but no roles still needs a role claim, or authorization
                // policies that require any role reject them outright.
                if (roles.Count == 0)
                    u.Claims.Add(new(ClaimTypes.Role, "User"));

                foreach (var claim in extraClaims ?? Enumerable.Empty<Claim>())
                    u.Claims.Add(claim);
            });

        return TokenIssueResult.Granted(token, jti, expiresAt, roles);
    }

    /// <summary>
    /// Returns null when the user may hold a token for this tenant, otherwise the reason they may not.
    ///
    /// Mirrors the check <c>SwitchTenantEndpoint</c> already performed — that endpoint was the only
    /// one of the four getting this right, so its behaviour is the reference.
    /// </summary>
    private async Task<string?> CheckTenantAccessAsync(User user, string slug, CancellationToken ct)
    {
        // The default tenant is the single-tenant/global context. There are no Membership rows for
        // it by design, so requiring one would lock out every non-multi-tenant deployment.
        if (slug == Tenant.DefaultSlug)
            return null;

        var tenant = await _session.Query<Tenant>()
            .FirstOrDefaultAsync(t => t.Slug == slug, ct);

        // An unregistered slug is not a managed tenant, so there is no membership model to enforce
        // against. This is the ordinary shape of a single-tenant deployment reached over a
        // subdomain: TenantResolutionMiddleware derives a slug from the host, nobody ever created a
        // Tenant document, and every user legitimately works in that partition. Denying it locks
        // out the whole deployment — which is exactly what happened to the public playground the
        // first time this check shipped.
        //
        // The vulnerability this guards against is obtaining a token for someone else's *real*
        // tenant, and those are registered by definition, so they stay covered below.
        if (tenant is null)
            return null;

        if (!tenant.IsActive)
            return "tenant is inactive";

        var isMember = await _session.Query<Membership>()
            .AnyAsync(m => m.UserId == user.Id
                           && m.TenantSlug == slug
                           && m.Status == MembershipStatus.Active, ct);

        return isMember ? null : "no active membership";
    }
}
