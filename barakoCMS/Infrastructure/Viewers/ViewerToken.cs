using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using FastEndpoints.Security;
using Microsoft.IdentityModel.Tokens;

namespace barakoCMS.Infrastructure.Viewers;

/// <summary>Who a viewer token was minted for.</summary>
/// <param name="Subject">The caller's own identifier for the viewer, opaque to the CMS.</param>
internal sealed record ViewerIdentity(string Subject);

/// <summary>
/// A short-lived signed token that lets public delivery serve the Members audience (#912).
/// </summary>
/// <remarks>
/// Bound to a tenant and carrying a subject, signed with the access-token key but under a
/// <c>:viewer</c> audience, so the bearer scheme rejects it: it opens delivery of Members entries
/// and fields and no authoring route. Any failure returns null and the caller decides what that
/// means; it never widens what an anonymous read sees.
/// </remarks>
internal static class ViewerToken
{
    public const string Header = "X-Viewer-Token";
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromHours(1);
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromHours(24);

    private const string PurposeClaim = "purpose";
    private const string PurposeValue = "viewer";
    private const string TenantClaim = "vtenant";
    private const string SubjectClaim = "vsub";

    private static string Audience(IConfiguration config) => (config["JWT:Audience"] ?? string.Empty) + ":viewer";

    public static (string Token, DateTime ExpiresAt) Create(
        IConfiguration config, string tenant, string subject, TimeSpan? lifetime = null)
    {
        var expiresAt = DateTime.UtcNow.Add(lifetime ?? DefaultLifetime);
        var token = JwtBearer.CreateToken(o =>
        {
            o.SigningKey = config["JWT:Key"]!;
            o.ExpireAt = expiresAt;
            o.Issuer = config["JWT:Issuer"];
            o.Audience = Audience(config);
            o.User.Claims.Add(new(PurposeClaim, PurposeValue));
            o.User.Claims.Add(new(TenantClaim, tenant));
            o.User.Claims.Add(new(SubjectClaim, subject));
        });
        return (token, expiresAt);
    }

    /// <summary>The viewer the token names, or null when it does not verify for this tenant.</summary>
    public static ViewerIdentity? Validate(IConfiguration config, string? token, string tenant)
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
            if (Claim(principal, PurposeClaim) != PurposeValue) return null;
            if (!string.Equals(Claim(principal, TenantClaim), tenant, StringComparison.OrdinalIgnoreCase)) return null;
            return Claim(principal, SubjectClaim) is { Length: > 0 } sub ? new ViewerIdentity(sub) : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? Claim(ClaimsPrincipal p, string type) => p.FindFirst(type)?.Value;
}
