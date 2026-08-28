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
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private const string PurposeClaim = "purpose";
    private const string PurposeValue = "mfa";
    private const string UserClaim = "mfauid";

    private static string Audience(IConfiguration config) => (config["JWT:Audience"] ?? string.Empty) + ":mfa";

    public static (string Token, DateTime ExpiresAt) Create(IConfiguration config, Guid userId)
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
            });
        return (token, expiresAt);
    }

    /// <summary>
    /// Returns the user id when the token verifies (signature + expiry + <c>:mfa</c> audience + purpose);
    /// otherwise null. Any failure returns null so the caller responds with a generic error.
    /// </summary>
    public static Guid? ValidatedUserId(IConfiguration config, string token)
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
            return Guid.TryParse(principal.FindFirst(UserClaim)?.Value, out var id) ? id : null;
        }
        catch
        {
            return null;
        }
    }
}
