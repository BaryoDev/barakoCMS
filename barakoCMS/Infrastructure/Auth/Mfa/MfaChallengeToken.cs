using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using FastEndpoints.Security;
using Microsoft.IdentityModel.Tokens;

namespace barakoCMS.Infrastructure.Auth.Mfa;

/// <summary>
/// Short-lived, signed token issued when a password is correct but the account requires a second factor.
/// It proves "the password was just verified for this user" and binds the pending second step to that
/// user, so the client never re-sends the password. Signed with the access-token key but a distinct
/// <c>:mfa</c> audience — so the API's bearer scheme rejects it (it is not an access credential, only a
/// grant to complete MFA). Mirrors <see cref="barakoCMS.Infrastructure.Preview.PreviewToken"/>.
/// </summary>
public static class MfaChallengeToken
{
    private const string GrantClaim = "mfagrant";
    private const int MaxGrantRoles = 100;

    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private const string PurposeClaim = "purpose";
    private const string PurposeValue = "mfa";
    private const string UserClaim = "mfauid";

    private static string Audience(IConfiguration config) => (config["JWT:Audience"] ?? string.Empty) + ":mfa";

    public static (string Token, DateTime ExpiresAt) Create(IConfiguration config, Guid userId) =>
        Create(config, userId, null);

    /// <summary>
    /// As <see cref="Create(IConfiguration, Guid)"/>, carrying the roles an external sign-in's claims
    /// earned, to be granted once the second factor passes. The token is signed, so the client
    /// cannot add to the list, and it expires with the challenge. Roles are checked again at
    /// verify, not trusted from here.
    /// </summary>
    public static (string Token, DateTime ExpiresAt) Create(IConfiguration config, Guid userId, PendingExternalGrant? grant)
    {
        var expiresAt = DateTime.UtcNow.Add(Lifetime);
        var token = JwtBearer.CreateToken(o =>
        {
            o.SigningKey = config["JWT:Key"]!;
            o.ExpireAt = expiresAt;
            o.Issuer = config["JWT:Issuer"];
            o.Audience = Audience(config);
            var u = o.User;
                u.Claims.Add(new(PurposeClaim, PurposeValue));
                u.Claims.Add(new(UserClaim, userId.ToString()));
                if (grant is { RoleIds.Count: > 0 and <= MaxGrantRoles })
                {
                    u.Claims.Add(new(GrantClaim, grant.Encode()));
                }
            });
        return (token, expiresAt);
    }

    /// <summary>
    /// Returns the user id when the token verifies (signature + expiry + <c>:mfa</c> audience + purpose);
    /// otherwise null. Any failure returns null so the caller responds with a generic error.
    /// </summary>
    public static Guid? ValidatedUserId(IConfiguration config, string token) =>
        Validated(config, token)?.UserId;

    /// <summary>The user the challenge was minted for and any external grant it carries, or null when it does not verify.</summary>
    public static (Guid UserId, PendingExternalGrant? Grant)? Validated(IConfiguration config, string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        var key = config["JWT:Key"];
        if (string.IsNullOrEmpty(key)) return null;

        var parameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            ValidateIssuer = !string.IsNullOrEmpty(config["JWT:Issuer"]),
            ValidIssuer = config["JWT:Issuer"],
            ValidateAudience = true,
            ValidAudience = Audience(config),
        };

        try
        {
            var principal = new JwtSecurityTokenHandler().ValidateToken(token, parameters, out _);
            if (principal.FindFirst(PurposeClaim)?.Value != PurposeValue) return null;
            if (!Guid.TryParse(principal.FindFirst(UserClaim)?.Value, out var id)) return null;
            return (id, PendingExternalGrant.Decode(principal.FindFirst(GrantClaim)?.Value));
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Roles in one tenant that an external identity provider's claims earned at first factor, carried by
/// the MFA challenge and granted at verify. Plain data, so core does not reference the module that
/// computed it.
/// </summary>
public sealed record PendingExternalGrant(string TenantSlug, string Source, IReadOnlyList<Guid> RoleIds)
{
    internal string Encode() => $"{TenantSlug}|{Source}|{string.Join(',', RoleIds)}";

    internal static PendingExternalGrant? Decode(string? value)
    {
        var parts = value?.Split('|');
        if (parts is not { Length: 3 })
        {
            return null;
        }

        var ids = parts[2].Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => Guid.TryParse(p, out var id) ? id : Guid.Empty).ToList();
        return ids.Count == 0 || ids.Contains(Guid.Empty) ? null : new PendingExternalGrant(parts[0], parts[1], ids);
    }
}
