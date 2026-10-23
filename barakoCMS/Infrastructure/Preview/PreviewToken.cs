using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using barakoCMS.Core.Interfaces;
using FastEndpoints.Security;
using Microsoft.IdentityModel.Tokens;

namespace barakoCMS.Infrastructure.Preview;

/// <summary>
/// Short-lived, signed tokens that authorize previewing ONE draft entry on the public delivery API.
/// A token is bound to (tenant, content type, slug, entry id) and signed with the access-token key but a
/// distinct <c>:preview</c> audience — so the main bearer scheme rejects it (it isn't an API credential,
/// only a preview grant) and it can't be reused for a different entry, slug, or tenant. It never grants
/// field-level access: the public projection still strips non-Public fields and refuses Sensitive docs.
/// </summary>
public static class PreviewToken
{
    public const string QueryParam = "preview";
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(30);

    private const string PurposeClaim = "purpose";
    private const string PurposeValue = "preview";
    private const string TenantClaim = "ptenant";
    private const string TypeClaim = "ptype";
    private const string SlugClaim = "pslug";
    private const string EntryIdClaim = "pid";

    /// <summary>Audience distinct from real access tokens, so the API's bearer scheme won't accept this.</summary>
    private static string Audience(IConfiguration config) => (config["JWT:Audience"] ?? string.Empty) + ":preview";

    public static (string Token, DateTime ExpiresAt) Create(
        IConfiguration config, string tenant, string type, string slug, Guid entryId, TimeSpan? lifetime = null)
    {
        var expiresAt = DateTime.UtcNow.Add(lifetime ?? DefaultLifetime);
        var token = JwtBearer.CreateToken(o =>
        {
            o.SigningKey = config["JWT:Key"]!;
            o.ExpireAt = expiresAt;
            o.Issuer = config["JWT:Issuer"];
            o.Audience = Audience(config);
            var u = o.User;
                u.Claims.Add(new(PurposeClaim, PurposeValue));
                u.Claims.Add(new(TenantClaim, tenant));
                u.Claims.Add(new(TypeClaim, type));
                u.Claims.Add(new(SlugClaim, slug));
                u.Claims.Add(new(EntryIdClaim, entryId.ToString()));
            });
        return (token, expiresAt);
    }

    /// <summary>
    /// Returns the authorized entry id when the token verifies (signature + expiry + preview audience) AND
    /// was minted for exactly this tenant, type, and slug; otherwise null. Any failure returns null — the
    /// caller then falls back to normal published-only behavior, so an invalid token leaks nothing.
    /// </summary>
    public static Guid? ValidatedEntryId(IConfiguration config, string token, string tenant, string type, string slug)
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
            if (!Match(Claim(principal, TenantClaim), tenant)) return null;
            if (!Match(Claim(principal, TypeClaim), type)) return null;
            if (!Match(Claim(principal, SlugClaim), slug)) return null;
            return Guid.TryParse(Claim(principal, EntryIdClaim), out var id) ? id : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The signed link purpose preview tokens are issued under.</summary>
    public const string Purpose = PurposeValue;

    private sealed record Subject(string Tenant, string Type, string Slug, Guid EntryId);

    /// <summary>Issues a preview token as a <c>preview</c> signed link bound to tenant, type, slug and entry id.</summary>
    public static (string Token, DateTime ExpiresAt) Issue(
        ISignedLinkService links, string tenant, string type, string slug, Guid entryId, TimeSpan? lifetime = null)
    {
        var link = links.Issue(Purpose,
            JsonSerializer.Serialize(new Subject(tenant, type, slug, entryId)), lifetime ?? DefaultLifetime);
        return (link.Token, link.ExpiresAt.UtcDateTime);
    }

    /// <summary>
    /// Validates either format: a signed link (<c>payload~signature</c>) or, until it expires, the older
    /// JWT. Same rules as <see cref="ValidatedEntryId"/>: any failure, or a token for another tenant,
    /// type or slug, returns null.
    /// </summary>
    public static async Task<Guid?> ValidatedEntryIdAsync(
        ISignedLinkService links, IConfiguration config, string token, string tenant, string type, string slug,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        if (token.Contains('.')) return ValidatedEntryId(config, token, tenant, type, slug);

        var result = await links.ValidateAsync(Purpose, token, ct);
        if (!result.Succeeded) return null;
        try
        {
            var s = JsonSerializer.Deserialize<Subject>(result.Subject!);
            if (s is null || !Match(s.Tenant, tenant) || !Match(s.Type, type) || !Match(s.Slug, slug)) return null;
            return s.EntryId;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Claim(ClaimsPrincipal p, string type) => p.FindFirst(type)?.Value;
    private static bool Match(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
