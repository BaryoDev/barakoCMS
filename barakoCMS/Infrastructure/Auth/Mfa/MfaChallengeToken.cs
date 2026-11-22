using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
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

    private const string FactorClaim = "mfafactor";
    private const int MaxFactorLength = 64;

    private const string PurposeClaim = "purpose";
    private const string PurposeValue = "mfa";
    private const string UserClaim = "mfauid";

    private static string Audience(IConfiguration config) => (config["JWT:Audience"] ?? string.Empty) + ":mfa";

    public static (string Token, DateTime ExpiresAt) Create(IJwtMinter minter, IConfiguration config, Guid userId) =>
        Create(minter, config, userId, null);

    /// <summary>
    /// As <see cref="Create(IJwtMinter, IConfiguration, Guid)"/>, carrying the roles an external sign-in's claims
    /// earned, to be granted once the second factor passes. The token is signed, so the client
    /// cannot add to the list, and it expires with the challenge. Roles are checked again at
    /// verify, not trusted from here. The grant is given a fresh <see cref="PendingExternalGrant.Id"/>
    /// here, which verify records as spent with the grant, so one challenge grants at most once.
    /// </summary>
    public static (string Token, DateTime ExpiresAt) Create(IJwtMinter minter, IConfiguration config, Guid userId, PendingExternalGrant? grant) =>
        Create(minter, config, userId, grant, null);

    /// <summary>
    /// As above, naming the first factor (<c>password</c>, <c>code</c>, <c>link</c> or
    /// <c>social:&lt;provider&gt;</c>) so the session that verify starts records how it began.
    /// </summary>
    public static (string Token, DateTime ExpiresAt) Create(
        IJwtMinter minter, IConfiguration config, Guid userId, PendingExternalGrant? grant, string? firstFactor)
    {
        var expiresAt = DateTime.UtcNow.Add(Lifetime);
        var claims = new List<Claim>
        {
            new(PurposeClaim, PurposeValue),
            new(UserClaim, userId.ToString()),
        };
        if (!string.IsNullOrEmpty(firstFactor))
        {
            claims.Add(new(FactorClaim, firstFactor.Length > MaxFactorLength ? firstFactor[..MaxFactorLength] : firstFactor));
        }
        if (grant is { RoleIds.Count: > 0 and <= MaxGrantRoles })
        {
            claims.Add(new(GrantClaim, (grant with { Id = Guid.NewGuid() }).Encode()));
        }
        var token = minter.Mint(Audience(config), expiresAt, claims);
        return (token, expiresAt);
    }

    /// <summary>
    /// Returns the user id when the token verifies (signature + expiry + <c>:mfa</c> audience + purpose);
    /// otherwise null. Any failure returns null so the caller responds with a generic error.
    /// </summary>
    public static Guid? ValidatedUserId(IConfiguration config, string token) =>
        Validated(config, token)?.UserId;

    /// <summary>The user the challenge was minted for and any external grant it carries, or null when it does not verify.</summary>
    public static (Guid UserId, PendingExternalGrant? Grant)? Validated(IConfiguration config, string token) =>
        ValidatedChallenge(config, token) is { } c ? (c.UserId, c.Grant) : null;

    /// <summary>What a verified challenge carries: the user, any external grant and the first factor.</summary>
    internal sealed record Challenge(Guid UserId, PendingExternalGrant? Grant, string? FirstFactor);

    internal static Challenge? ValidatedChallenge(IConfiguration config, string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        var key = config["JWT:Key"];
        if (string.IsNullOrEmpty(key)) return null;

        var parameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(JwtKeySource.Bytes(key)),
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
            return new Challenge(id, PendingExternalGrant.Decode(principal.FindFirst(GrantClaim)?.Value),
                principal.FindFirst(FactorClaim)?.Value);
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
    /// <summary>
    /// Names this grant once it is in a challenge, so verify can spend it. Empty until
    /// <see cref="MfaChallengeToken.Create(IJwtMinter, IConfiguration, Guid, PendingExternalGrant?)"/> sets it.
    /// </summary>
    public Guid Id { get; init; }

    private sealed record Wire(Guid Id, string TenantSlug, string Source, List<Guid> RoleIds);

    /// <summary>JSON, so a slug or source holding any character reads back as it was written.</summary>
    internal string Encode() =>
        System.Text.Json.JsonSerializer.Serialize(new Wire(Id, TenantSlug, Source, RoleIds.ToList()));

    internal static PendingExternalGrant? Decode(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        Wire? wire;
        try
        {
            wire = System.Text.Json.JsonSerializer.Deserialize<Wire>(value);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }

        if (wire is not { TenantSlug: not null, Source: not null, RoleIds: { Count: > 0 } ids }
            || wire.Id == Guid.Empty || ids.Contains(Guid.Empty))
        {
            return null;
        }

        return new PendingExternalGrant(wire.TenantSlug, wire.Source, ids) { Id = wire.Id };
    }
}
