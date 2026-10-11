using System.IdentityModel.Tokens.Jwt;
using System.Text;
using FastEndpoints.Security;
using Microsoft.IdentityModel.Tokens;

namespace barakoCMS.Infrastructure.Auth.Mfa;

/// <summary>
/// Proof that this user entered a fresh second-factor code a few minutes ago. Signed with the access
/// token key under a distinct <c>:stepup</c> audience, so it is not an access token and an access token
/// is not one of these. Same construction as <see cref="MfaChallengeToken"/>.
/// </summary>
public static class StepUpToken
{
    private const string PurposeClaim = "purpose";
    private const string PurposeValue = "stepup";
    private const string UserClaim = "uid";

    private static string Audience(IConfiguration config) => (config["JWT:Audience"] ?? string.Empty) + ":stepup";

    public static TimeSpan Lifetime(IConfiguration config) =>
        TimeSpan.FromSeconds(Math.Clamp(config.GetValue("Auth:StepUp:Seconds", 300), 30, 3600));

    public static (string Token, DateTime ExpiresAt) Create(IConfiguration config, Guid userId)
    {
        var expiresAt = DateTime.UtcNow.Add(Lifetime(config));
        var token = JwtBearer.CreateToken(o =>
        {
            o.SigningKey = config["JWT:Key"]!;
            o.ExpireAt = expiresAt;
            o.Issuer = config["JWT:Issuer"];
            o.Audience = Audience(config);
            o.User.Claims.Add(new(PurposeClaim, PurposeValue));
            o.User.Claims.Add(new(UserClaim, userId.ToString()));
        });
        return (token, expiresAt);
    }

    /// <summary>The user id when the token verifies, otherwise null. Never throws.</summary>
    public static Guid? ValidatedUserId(IConfiguration config, string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        var key = config["JWT:Key"];
        if (string.IsNullOrEmpty(key)) return null;

        var parameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero,
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
